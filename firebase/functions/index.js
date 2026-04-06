const { onRequest } = require("firebase-functions/v2/https");
const { onSchedule: onScheduleV2 } = require("firebase-functions/v2/scheduler");
const { onValueWritten } = require("firebase-functions/v2/database");
const { defineSecret } = require("firebase-functions/params");
const admin = require("firebase-admin");

const discordWebhookUrl = defineSecret("DISCORD_WEBHOOK_URL");
const discordPlanarStdWebhookUrl = defineSecret("DISCORD_WEBHOOK_PLANAR_STD");
const discordPauperWebhookUrl = defineSecret("DISCORD_WEBHOOK_PAUPER");

admin.initializeApp({
  databaseURL: "https://mtga-enhancement-suite-default-rtdb.firebaseio.com",
});

const SCRYFALL_SEARCH_URL = "https://api.scryfall.com/cards/search";
const SCRYFALL_DELAY_MS = 100; // Scryfall asks for 50-100ms between requests

/**
 * Format registry — defines all supported formats.
 * The Scryfall query uses `legal:{key}` so the key must match Scryfall's format name.
 * Display names are what users see in the spinner.
 */
const FORMAT_REGISTRY = {
  pauper:             { displayName: "Pauper (Paper Banlist)", scryfallQuery: "legal:pauper" },
  // Historic Pauper is synced via tools/sync_pauper_from_mtga.py (reads MTGA local card DB).
  // Scryfall is missing some Arena-only common printings (e.g. Final Fantasy set).
  // Do NOT add historicpauper back to this registry — it will overwrite the correct local data.
  // historicpauper: { ... },
  standardpauper:     { displayName: "Standard Pauper",       scryfallQuery: '(game:arena) legal:standard (set:tmt OR set:ecl OR set:tla OR set:spm OR set:om1 OR set:eoe OR set:fin OR set:tdm OR set:dft OR set:dsk OR set:blb OR set:otj OR set:mkm OR set:lci OR set:woe OR set:fdn)', rawQuery: true, filterCommonPrints: true },
  planarstandard:     { displayName: "Planar Standard",       scryfallQuery: 'game:paper (set:ecl or set:eoe or set:tdm or set:dft or set:fdn) -name:"Cori-steel Cutter"', rawQuery: true },
  modern:             { displayName: "Modern",                scryfallQuery: "legal:modern" },
};

/**
 * Syncs ALL registered formats from Scryfall daily at 6:00 AM UTC.
 * Also writes the format registry to /formatList for clients to read.
 */
exports.syncAllFormats = onScheduleV2(
  { schedule: "0 6 * * *", timeZone: "UTC", memory: "1GiB", timeoutSeconds: 540 },
  async (event) => {
    await syncAllFormats();
  }
);

/**
 * HTTP-callable version for manual triggers / testing.
 * Pass ?format=pauper to sync a single format, or no param to sync all.
 */
exports.syncFormatsHttp = onRequest(
  { cors: true, memory: "1GiB", timeoutSeconds: 540 },
  async (req, res) => {
    const singleFormat = req.query.format || req.body?.format;
    if (singleFormat) {
      if (EXTERNAL_FORMATS[singleFormat]) {
        res.status(400).json({ error: `${singleFormat} is synced externally via tools/sync_pauper_from_mtga.py, not via Scryfall` });
        return;
      }
      if (!FORMAT_REGISTRY[singleFormat]) {
        res.status(400).json({ error: `Unknown format: ${singleFormat}`, available: Object.keys(FORMAT_REGISTRY) });
        return;
      }
      const meta = FORMAT_REGISTRY[singleFormat];
      const result = await syncFormatFromScryfall(singleFormat, meta.scryfallQuery, meta.rawQuery, meta.filterCommonPrints);
      res.json(result);
    } else {
      const results = await syncAllFormats();
      res.json(results);
    }
  }
);

/**
 * Syncs all formats and writes the format list for clients.
 */
// Formats synced externally (not via Scryfall). Still listed in the spinner.
const EXTERNAL_FORMATS = {
  historicpauper: { displayName: "Historic Pauper" },  // synced via tools/sync_pauper_from_mtga.py
};

async function syncAllFormats() {
  // Write the format list (clients read this to populate the spinner)
  const formatList = {};
  for (const [key, meta] of Object.entries(FORMAT_REGISTRY)) {
    formatList[key] = { displayName: meta.displayName };
  }
  for (const [key, meta] of Object.entries(EXTERNAL_FORMATS)) {
    formatList[key] = { displayName: meta.displayName };
  }
  await admin.database().ref("formatList").set(formatList);
  console.log(`Format list written: ${Object.keys(formatList).join(", ")}`);

  // Sync each format's legal cards
  const results = [];
  for (const [key, meta] of Object.entries(FORMAT_REGISTRY)) {
    try {
      const result = await syncFormatFromScryfall(key, meta.scryfallQuery, meta.rawQuery, meta.filterCommonPrints);
      results.push(result);
    } catch (err) {
      console.error(`Failed to sync ${key}: ${err.message}`);
      results.push({ format: key, error: err.message });
    }
  }
  return results;
}

/**
 * Fetches all cards legal in a format that exist on Arena from Scryfall,
 * then writes the arena_id -> card name mapping to Firebase.
 */
async function syncFormatFromScryfall(format, scryfallQuery, rawQuery, filterCommonPrints) {
  const query = scryfallQuery || `legal:${format}`;
  // rawQuery means the query already includes game: filter — don't append game:arena
  const fullQuery = rawQuery ? query : `${query} game:arena`;
  console.log(`Starting ${format} sync from Scryfall (query: ${fullQuery}, filterCommon: ${!!filterCommonPrints})...`);

  // Dual storage: arena_ids for language-independent checks,
  // names as fallback for cards without arena_ids (e.g. om1 crossover cards)
  const legalArenaIds = {}; // arena_id (string) -> true
  const legalNames = {};    // lowercase card name -> true
  let page = 1;
  let hasMore = true;
  let totalFetched = 0;
  let arenaIdCount = 0;

  // When filterCommonPrints is true, we fetch all prints and only include
  // cards that have at least one common printing. This handles cards like
  // Cultivate that have both common and uncommon printings on Arena.
  const uniqueMode = filterCommonPrints ? "prints" : "cards";
  // Track which card names have a common printing (for filterCommonPrints)
  const namesWithCommonPrint = new Set();
  // Buffer all cards by name so we can filter after the full fetch
  const cardsByName = {}; // name -> [card, card, ...]

  // First pass
  while (hasMore) {
    const url = `${SCRYFALL_SEARCH_URL}?q=${encodeURIComponent(fullQuery)}&unique=${uniqueMode}&format=json&page=${page}`;

    const response = await fetch(url);

    if (!response.ok) {
      if (response.status === 429) {
        console.log("Rate limited, waiting 1s...");
        await sleep(1000);
        continue;
      }
      throw new Error(`Scryfall API error: ${response.status} ${response.statusText}`);
    }

    const data = await response.json();

    for (const card of data.data) {
      const name = card.name.toLowerCase();

      if (filterCommonPrints) {
        // Buffer cards by name and track which have common prints
        if (!cardsByName[name]) cardsByName[name] = [];
        cardsByName[name].push(card);
        if (card.rarity === "common") {
          namesWithCommonPrint.add(name);
        }
      } else {
        // Original behavior: add all cards directly
        addCardToLists(card, legalArenaIds, legalNames);
        arenaIdCount += (card.arena_id ? 1 : 0);
      }
    }

    totalFetched += data.data.length;
    hasMore = data.has_more;
    page++;

    // Respect Scryfall rate limits
    await sleep(SCRYFALL_DELAY_MS);
  }

  // For filterCommonPrints: now process the buffered cards, only including
  // those with at least one common printing. All printings of qualifying
  // cards are included (so all arena_ids are captured).
  if (filterCommonPrints) {
    console.log(`First pass done: ${totalFetched} prints, ${namesWithCommonPrint.size} cards with common printings.`);
    for (const [name, cards] of Object.entries(cardsByName)) {
      if (!namesWithCommonPrint.has(name)) continue; // no common printing = skip
      for (const card of cards) {
        addCardToLists(card, legalArenaIds, legalNames);
        arenaIdCount += (card.arena_id ? 1 : 0);
      }
    }
  }

  // Second pass: unique=prints to collect ALL arena_ids across all printings.
  // Skip if we already used unique=prints in the first pass (filterCommonPrints).
  if (!filterCommonPrints) {
    console.log(`First pass done: ${totalFetched} cards. Collecting all arena_ids from prints...`);
    page = 1;
    hasMore = true;

    while (hasMore) {
      const url = `${SCRYFALL_SEARCH_URL}?q=${encodeURIComponent(fullQuery)}&unique=prints&format=json&page=${page}`;

      const response = await fetch(url);

      if (!response.ok) {
        if (response.status === 429) {
          console.log("Rate limited, waiting 1s...");
          await sleep(1000);
          continue;
        }
        console.log(`Prints query failed on page ${page}: ${response.status}, using first-pass arena_ids`);
        break;
      }

      const data = await response.json();

      for (const card of data.data) {
        if (card.arena_id) {
          if (!legalArenaIds[String(card.arena_id)]) {
            legalArenaIds[String(card.arena_id)] = true;
            arenaIdCount++;
          }
        }
      }

      hasMore = data.has_more;
      page++;
      await sleep(SCRYFALL_DELAY_MS);
    }
  }

  console.log(`Fetched ${totalFetched} unique cards, ${arenaIdCount} arena_ids, ${Object.keys(legalNames).length} name entries`);

  // Write to Firebase in one batch
  const update = {
    legalArenaIds,
    legalNames,
    lastSync: admin.database.ServerValue.TIMESTAMP,
    totalCards: totalFetched,
    totalArenaIds: arenaIdCount,
    totalNames: Object.keys(legalNames).length,
  };

  await admin.database().ref(`formats/${format}`).set(update);

  const result = {
    format,
    totalCards: totalFetched,
    syncedAt: new Date().toISOString(),
  };

  console.log(`${format} sync complete:`, result);
  return result;
}

/**
 * Adds a card's arena_id and name variants to the legal lists.
 */
function addCardToLists(card, legalArenaIds, legalNames) {
  const name = card.name.toLowerCase();

  if (card.arena_id) {
    legalArenaIds[String(card.arena_id)] = true;
  }

  legalNames[encodeFirebaseKey(name)] = true;

  // Individual faces for DFCs/split cards
  if (name.includes(" // ")) {
    for (const face of name.split(" // ")) {
      legalNames[encodeFirebaseKey(face.trim())] = true;
    }
  }

  // printed_name for IP crossover cards (om1 Marvel set etc.)
  if (card.printed_name) {
    const printedName = card.printed_name.toLowerCase();
    legalNames[encodeFirebaseKey(printedName)] = true;
    if (printedName.includes(" // ")) {
      for (const face of printedName.split(" // ")) {
        legalNames[encodeFirebaseKey(face.trim())] = true;
      }
    }
  }

  // Card faces for DFCs with separate printed_names
  if (card.card_faces) {
    for (const face of card.card_faces) {
      if (face.name) {
        legalNames[encodeFirebaseKey(face.name.toLowerCase())] = true;
      }
      if (face.printed_name) {
        legalNames[encodeFirebaseKey(face.printed_name.toLowerCase())] = true;
      }
    }
  }
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// Firebase keys can't contain . $ # [ ] /
function encodeFirebaseKey(key) {
  return key
    .replace(/%/g, "%25")
    .replace(/\./g, "%2E")
    .replace(/\$/g, "%24")
    .replace(/#/g, "%23")
    .replace(/\[/g, "%5B")
    .replace(/\]/g, "%5D")
    .replace(/\//g, "%2F");
}

function decodeFirebaseKey(key) {
  return key
    .replace(/%2F/gi, "/")
    .replace(/%5D/gi, "]")
    .replace(/%5B/gi, "[")
    .replace(/%23/gi, "#")
    .replace(/%24/gi, "$")
    .replace(/%2E/gi, ".")
    .replace(/%25/g, "%");
}

/**
 * Issues a Firebase custom auth token for an MTGA player.
 * The plugin calls this with the player's PersonaID and DisplayName
 * so all subsequent Firebase actions are tied to their MTGA identity.
 */
exports.getAuthToken = onRequest({ cors: true }, async (req, res) => {
  if (req.method !== "POST") {
    res.status(405).json({ error: "Method not allowed" });
    return;
  }

  const { personaId, displayName } = req.body;

  if (!personaId || !displayName) {
    res.status(400).json({ error: "Missing personaId or displayName" });
    return;
  }

  try {
    let userRecord;
    try {
      userRecord = await admin.auth().getUser(personaId);
      if (userRecord.displayName !== displayName) {
        await admin.auth().updateUser(personaId, { displayName });
      }
    } catch (err) {
      if (err.code === "auth/user-not-found") {
        userRecord = await admin.auth().createUser({
          uid: personaId,
          displayName,
        });
      } else {
        throw err;
      }
    }

    const customToken = await admin.auth().createCustomToken(personaId, {
      displayName,
    });

    res.json({ token: customToken });
  } catch (err) {
    console.error("Error creating auth token:", err);
    res.status(500).json({ error: "Failed to create auth token" });
  }
});

/**
 * Validates a deck against the stored legal cards list for a lobby's format.
 * Checks arena_ids against /formats/{format}/legalArenaIds, falls back to legalNames.
 */
exports.validateDeck = onRequest({ cors: true }, async (req, res) => {
  if (req.method !== "POST") {
    res.status(405).json({ error: "Method not allowed" });
    return;
  }

  const { challengeId, format: bodyFormat, decklist } = req.body;

  if (!decklist || !Array.isArray(decklist)) {
    res.status(400).json({ error: "Missing decklist array" });
    return;
  }

  // Format can come from the request body directly, or looked up from the lobby
  let format = bodyFormat;
  if (!format && challengeId) {
    const lobbySnap = await admin
      .database()
      .ref(`lobbies/${challengeId}`)
      .once("value");

    if (lobbySnap.exists()) {
      format = lobbySnap.val().format;
    }
  }

  if (!format) {
    res.status(400).json({ error: "No format specified and no lobby found" });
    return;
  }

  if (!format || format === "none") {
    res.json({ valid: true });
    return;
  }

  // Get the legal arena IDs and name fallbacks for this format
  const [idsSnap, namesSnap] = await Promise.all([
    admin.database().ref(`formats/${format}/legalArenaIds`).once("value"),
    admin.database().ref(`formats/${format}/legalNames`).once("value"),
  ]);

  if (!idsSnap.exists() && !namesSnap.exists()) {
    res.status(500).json({
      error: `Legal cards list for ${format} not synced yet. Run syncFormatsHttp first.`,
    });
    return;
  }

  const legalArenaIds = idsSnap.val() || {};
  const legalNames = namesSnap.val() || {};

  const illegalCards = [];

  for (const card of decklist) {
    const arenaId = String(card.grpId);

    // Skip basic lands (rarity 1)
    if (card.rarityValue === 1) continue;

    // Primary: check arena_id
    if (legalArenaIds[arenaId]) continue;

    // Fallback: check card name (for cards without arena_id)
    if (card.name) {
      const encodedName = encodeFirebaseKey(card.name.toLowerCase());
      if (legalNames[encodedName]) continue;
    }

    illegalCards.push({
      grpId: card.grpId,
      name: card.name,
      quantity: card.quantity,
      rarity: card.rarityName || "Unknown",
    });
  }

  if (illegalCards.length === 0) {
    res.json({ valid: true });
  } else {
    res.json({
      valid: false,
      format,
      illegalCards,
    });
  }
});

/**
 * Lists public lobbies for the web lobby browser.
 * Returns only active, public lobbies with fresh heartbeats.
 */
exports.listPublicLobbies = onRequest({ cors: true }, async (req, res) => {
  const now = Math.floor(Date.now() / 1000);
  const staleThreshold = now - 120; // 2 minutes

  const lobbiesSnap = await admin.database().ref("lobbies").once("value");
  if (!lobbiesSnap.exists()) {
    res.json({ lobbies: [] });
    return;
  }

  const lobbies = lobbiesSnap.val();
  const result = [];

  for (const [id, lobby] of Object.entries(lobbies)) {
    if (!lobby.isPublic) continue;
    const lastHeartbeat = lobby.lastHeartbeat || lobby.createdAt || 0;
    if (lastHeartbeat < staleThreshold) continue;

    result.push({
      id,
      host: lobby.hostDisplayName || "Unknown",
      format: lobby.format || "none",
      isBestOf3: lobby.isBestOf3 || false,
      createdAt: lobby.createdAt || 0,
    });
  }

  res.json({ lobbies: result });
});

/**
 * Accepts a join request from a non-mod user on the web page.
 * Writes to /lobbies/{challengeId}/joinRequests/{pushId} so the
 * host's SSE listener can pick it up and send an in-game invite.
 */
exports.submitJoinRequest = onRequest({ cors: true }, async (req, res) => {
  if (req.method !== "POST") {
    res.status(405).json({ error: "Method not allowed" });
    return;
  }

  const { challengeId, username } = req.body;

  if (!challengeId || !username) {
    res.status(400).json({ error: "Missing challengeId or username" });
    return;
  }

  // Validate username format: Name#12345
  if (!/^.+#\d+$/.test(username)) {
    res.status(400).json({ error: "Invalid username format. Use Name#12345" });
    return;
  }

  // Check lobby exists and is open
  const lobbySnap = await admin.database().ref(`lobbies/${challengeId}`).once("value");
  if (!lobbySnap.exists()) {
    res.status(404).json({ error: "Lobby not found or no longer available" });
    return;
  }

  const lobby = lobbySnap.val();
  if (lobby.status !== "open") {
    res.status(404).json({ error: "Lobby is no longer available" });
    return;
  }

  // Check for duplicate pending request from same username
  const existingSnap = await admin.database().ref(`lobbies/${challengeId}/joinRequests`)
    .orderByChild("username").equalTo(username).once("value");

  if (existingSnap.exists()) {
    const existing = existingSnap.val();
    for (const [key, req] of Object.entries(existing)) {
      if (req.status === "pending") {
        res.json({ success: true, requestId: key, message: "Request already pending" });
        return;
      }
    }
  }

  // Write join request
  const ref = admin.database().ref(`lobbies/${challengeId}/joinRequests`).push();
  await ref.set({
    username: username,
    timestamp: admin.database.ServerValue.TIMESTAMP,
    status: "pending",
  });

  console.log(`Join request: ${username} → lobby ${challengeId} (${ref.key})`);
  res.json({ success: true, requestId: ref.key });
});

/**
 * Cleans up stale lobbies every 30 minutes.
 * Deletes any lobby with lastHeartbeat older than 5 minutes.
 */
exports.cleanStaleLobbies = onScheduleV2(
  { schedule: "*/5 * * * *", timeZone: "UTC", secrets: [discordWebhookUrl, discordPlanarStdWebhookUrl, discordPauperWebhookUrl] },
  async (event) => {
    const now = Math.floor(Date.now() / 1000);
    const staleThreshold = now - 3600; // 60 minutes — must outlast Bo3 matches

    const lobbiesSnap = await admin.database().ref("lobbies").once("value");
    if (!lobbiesSnap.exists()) return;

    const lobbies = lobbiesSnap.val();
    const staleIds = [];

    for (const [id, lobby] of Object.entries(lobbies)) {
      const lastHeartbeat = lobby.lastHeartbeat || lobby.createdAt || 0;
      if (lastHeartbeat < staleThreshold) {
        staleIds.push(id);
      }
    }

    if (staleIds.length === 0) return;

    // Expire Discord messages before deleting lobbies
    const secrets = {
      general: discordWebhookUrl.value(),
      planar: discordPlanarStdWebhookUrl.value(),
      pauper: discordPauperWebhookUrl.value(),
    };

    for (const id of staleIds) {
      const lobby = lobbies[id];
      if (lobby.discordMessages) {
        console.log(`Expiring Discord messages for stale lobby ${id}`);
        try {
          await expireDiscordMessages(id, lobby, secrets);
        } catch (err) {
          console.error(`Failed to expire messages for ${id}: ${err.message}`);
        }
      }
      console.log(`Deleting stale lobby ${id} (last heartbeat: ${lobby.lastHeartbeat || lobby.createdAt || 0})`);
    }

    // Delete all stale lobbies
    const deletions = staleIds.map(id => admin.database().ref(`lobbies/${id}`).remove());
    await Promise.all(deletions);
    console.log(`Cleaned up ${staleIds.length} stale lobbies`);
  }
);

// --- Discord Webhook Helpers ---

const PAUPER_FORMATS = ["pauper", "historicpauper", "standardpauper"];
const PAUPER_DISPLAY_NAMES = { pauper: "Vintage Pauper", historicpauper: "Historic Pauper", standardpauper: "Standard Pauper" };

/**
 * Sends a Discord webhook message with ?wait=true to get the message ID back.
 * Returns the message ID or null on failure.
 */
async function sendWebhookMessage(webhookUrl, payload) {
  const url = webhookUrl.includes("?") ? `${webhookUrl}&wait=true` : `${webhookUrl}?wait=true`;
  const resp = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
  if (!resp.ok) {
    console.error(`Webhook send failed: ${resp.status} ${await resp.text()}`);
    return null;
  }
  const data = await resp.json();
  return data.id || null;
}

/**
 * Edits an existing Discord webhook message.
 */
async function editWebhookMessage(webhookUrl, messageId, payload) {
  // Extract webhook ID and token from URL: https://discord.com/api/webhooks/{id}/{token}
  const match = webhookUrl.match(/webhooks\/(\d+)\/([^/?]+)/);
  if (!match) {
    console.error("Could not parse webhook URL for editing");
    return;
  }
  const editUrl = `https://discord.com/api/webhooks/${match[1]}/${match[2]}/messages/${messageId}`;
  const resp = await fetch(editUrl, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
  if (!resp.ok) {
    console.error(`Webhook edit failed: ${resp.status} ${await resp.text()}`);
  }
}

/**
 * Stores Discord message IDs for a lobby so they can be edited later.
 */
async function storeMessageIds(lobbyId, channel, embedMsgId, usernameMsgId) {
  const updates = {};
  if (embedMsgId) updates[`lobbies/${lobbyId}/discordMessages/${channel}/embed`] = embedMsgId;
  if (usernameMsgId) updates[`lobbies/${lobbyId}/discordMessages/${channel}/username`] = usernameMsgId;
  if (Object.keys(updates).length > 0) {
    await admin.database().ref().update(updates);
  }
}

/**
 * Edits all tracked Discord messages for a lobby to show "Lobby Closed".
 */
async function expireDiscordMessages(lobbyId, lobby, secrets) {
  const messages = lobby.discordMessages;
  if (!messages) return;

  const host = lobby.hostDisplayName || "Unknown";
  const format = lobby.format || "none";

  const webhookMap = {
    general: secrets.general,
    planar: secrets.planar,
    pauper: secrets.pauper,
  };

  for (const [channel, msgIds] of Object.entries(messages)) {
    const webhookUrl = webhookMap[channel];
    if (!webhookUrl) continue;

    try {
      // Edit the embed message to show closed
      if (msgIds.embed) {
        await editWebhookMessage(webhookUrl, msgIds.embed, {
          embeds: [{
            title: "🚫 Lobby Closed",
            color: 0x666666,
            description: `~~${host}'s ${format} lobby~~ — This lobby is no longer available.\n\n[Learn more about MTGA+](https://github.com/MayerDaniel/MTGA-Enhancement-Suite)`,
            timestamp: new Date().toISOString(),
          }],
        });
      }

      // Edit the username message to show closed
      if (msgIds.username) {
        await editWebhookMessage(webhookUrl, msgIds.username, {
          content: `~~${host}~~ — lobby closed`,
        });
      }

      console.log(`Expired Discord messages for ${channel} channel (lobby ${lobbyId})`);
    } catch (err) {
      console.error(`Failed to expire ${channel} messages for lobby ${lobbyId}: ${err.message}`);
    }
  }

  // Clean up discordMessages from Firebase so they don't get re-expired
  try {
    const ref = admin.database().ref(`lobbies/${lobbyId}/discordMessages`);
    const snap = await ref.once("value");
    if (snap.exists()) {
      await ref.remove();
      console.log(`Cleaned up discordMessages for lobby ${lobbyId}`);
    }
  } catch (err) {
    // Lobby may already be deleted, that's fine
    console.log(`Could not clean discordMessages for ${lobbyId} (may already be deleted): ${err.message}`);
  }
}

/**
 * Sends a Discord notification when a lobby becomes public.
 * Tracks message IDs for later editing when the lobby closes.
 */
exports.notifyDiscordOnPublicLobby = onValueWritten(
  {
    ref: "/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordWebhookUrl],
  },
  async (event) => {
    const before = event.data.before.val();
    const after = event.data.after.val();

    if (!after || !after.isPublic) return;
    if (before && before.isPublic) return;

    const lobby = after;
    const lobbyId = event.params.lobbyId;
    const host = lobby.hostDisplayName || "Unknown";
    const hostFull = lobby.hostFullName || host;
    const format = lobby.format || "none";
    const formatDisplay = format === "none" ? "No Format" : format.charAt(0).toUpperCase() + format.slice(1);
    const bestOf = lobby.isBestOf3 ? "Bo3" : "Bo1";
    const joinUrl = `https://mtga-enhancement-suite.web.app/join/${lobbyId}?format=${encodeURIComponent(format)}`;

    const webhookUrl = discordWebhookUrl.value();
    if (!webhookUrl) return;

    try {
      const embedMsgId = await sendWebhookMessage(webhookUrl, {
        embeds: [{
          title: "🎮 New Public Lobby",
          color: 0x00ff88,
          fields: [
            { name: "Host", value: host, inline: true },
            { name: "Format", value: formatDisplay, inline: true },
            { name: "Best Of", value: bestOf, inline: true },
          ],
          url: joinUrl,
          description: `**[Click to join](${joinUrl})**`,
          timestamp: new Date().toISOString(),
        }],
      });

      const usernameMsgId = await sendWebhookMessage(webhookUrl, {
        content: `${hostFull}`,
      });

      await storeMessageIds(lobbyId, "general", embedMsgId, usernameMsgId);
      console.log(`Discord notified: ${host} hosting ${formatDisplay} (${lobbyId}), msgIds: ${embedMsgId}, ${usernameMsgId}`);
    } catch (err) {
      console.error(`Discord webhook error: ${err.message}`);
    }
  }
);

/**
 * Sends a Discord notification to the Planar Standard channel.
 */
exports.notifyDiscordPlanarStandard = onValueWritten(
  {
    ref: "/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordPlanarStdWebhookUrl],
  },
  async (event) => {
    const before = event.data.before.val();
    const after = event.data.after.val();

    if (!after || !after.isPublic) return;
    if (before && before.isPublic) return;
    if (after.format !== "planarstandard") return;

    const lobbyId = event.params.lobbyId;
    const host = after.hostDisplayName || "Unknown";
    const hostFull = after.hostFullName || host;
    const bestOf = after.isBestOf3 ? "Bo3" : "Bo1";
    const joinUrl = `https://mtga-enhancement-suite.web.app/join/${lobbyId}?format=planarstandard`;

    const webhookUrl = discordPlanarStdWebhookUrl.value();
    if (!webhookUrl) return;

    try {
      const embedMsgId = await sendWebhookMessage(webhookUrl, {
        embeds: [{
          title: "⚔️ Planar Standard Lobby Open",
          color: 0xf5a623,
          fields: [
            { name: "Host", value: host, inline: true },
            { name: "Format", value: "Planar Standard", inline: true },
            { name: "Best Of", value: bestOf, inline: true },
          ],
          url: joinUrl,
          description: `**[Click to join](${joinUrl})**`,
          timestamp: new Date().toISOString(),
        }],
      });

      const usernameMsgId = await sendWebhookMessage(webhookUrl, {
        content: `<@&1429556399473557755> ${hostFull}`,
      });

      await storeMessageIds(lobbyId, "planar", embedMsgId, usernameMsgId);
      console.log(`Planar Std Discord notified: ${host} (${lobbyId}), msgIds: ${embedMsgId}, ${usernameMsgId}`);
    } catch (err) {
      console.error(`Planar Std Discord webhook error: ${err.message}`);
    }
  }
);

/**
 * Sends a Discord notification to the Pauper channel.
 */
exports.notifyDiscordPauper = onValueWritten(
  {
    ref: "/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordPauperWebhookUrl],
  },
  async (event) => {
    const before = event.data.before.val();
    const after = event.data.after.val();

    if (!after || !after.isPublic) return;
    if (before && before.isPublic) return;
    if (!PAUPER_FORMATS.includes(after.format)) return;

    const lobbyId = event.params.lobbyId;
    const host = after.hostDisplayName || "Unknown";
    const hostFull = after.hostFullName || host;
    const format = after.format || "pauper";
    const formatDisplay = PAUPER_DISPLAY_NAMES[format] || FORMAT_REGISTRY[format]?.displayName || format;
    const bestOf = after.isBestOf3 ? "Bo3" : "Bo1";
    const joinUrl = `https://mtga-enhancement-suite.web.app/join/${lobbyId}?format=${encodeURIComponent(format)}`;

    const webhookUrl = discordPauperWebhookUrl.value();
    if (!webhookUrl) return;

    try {
      const embedMsgId = await sendWebhookMessage(webhookUrl, {
        embeds: [{
          title: "🃏 Pauper Lobby Open",
          color: 0x8b6914,
          fields: [
            { name: "Host", value: host, inline: true },
            { name: "Format", value: formatDisplay, inline: true },
            { name: "Best Of", value: bestOf, inline: true },
          ],
          url: joinUrl,
          description: `**[Click to join](${joinUrl})**`,
          timestamp: new Date().toISOString(),
        }],
      });

      const usernameMsgId = await sendWebhookMessage(webhookUrl, {
        content: `${hostFull}`,
      });

      await storeMessageIds(lobbyId, "pauper", embedMsgId, usernameMsgId);
      console.log(`Pauper Discord notified: ${host} hosting ${formatDisplay} (${lobbyId}), msgIds: ${embedMsgId}, ${usernameMsgId}`);
    } catch (err) {
      console.error(`Pauper Discord webhook error: ${err.message}`);
    }
  }
);

/**
 * Expires Discord messages when a lobby is deleted or set to private.
 * Fires on any write to /lobbies/{lobbyId}.
 */
exports.expireDiscordOnLobbyClose = onValueWritten(
  {
    ref: "/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordWebhookUrl, discordPlanarStdWebhookUrl, discordPauperWebhookUrl],
  },
  async (event) => {
    const before = event.data.before.val();
    const after = event.data.after.val();

    // Check both before and after for discordMessages (after has them on re-publicize)
    const lobbyData = before || {};
    const afterData = after || {};

    // Get discordMessages from whichever has them
    const discordMessages = lobbyData.discordMessages || afterData.discordMessages;
    if (!discordMessages) return; // no tracked messages anywhere

    const lobbyDeleted = !after;
    const lobbyWentPrivate = after && !after.isPublic && (before && before.isPublic);

    if (!lobbyDeleted && !lobbyWentPrivate) return;

    const lobbyId = event.params.lobbyId;
    const messageChannels = Object.keys(discordMessages);
    console.log(`Lobby ${lobbyId} closed (deleted=${lobbyDeleted}, wentPrivate=${lobbyWentPrivate}), expiring Discord messages for channels: ${messageChannels.join(", ")}`);

    // Use lobby data from whichever side has it for display info
    const displayLobby = { ...lobbyData, ...afterData, discordMessages };

    await expireDiscordMessages(lobbyId, displayLobby, {
      general: discordWebhookUrl.value(),
      planar: discordPlanarStdWebhookUrl.value(),
      pauper: discordPauperWebhookUrl.value(),
    });
  }
);
