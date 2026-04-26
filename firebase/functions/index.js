const { onRequest } = require("firebase-functions/v2/https");
const { onSchedule: onScheduleV2 } = require("firebase-functions/v2/scheduler");
const { onValueWritten } = require("firebase-functions/v2/database");
const { defineSecret } = require("firebase-functions/params");
const admin = require("firebase-admin");
const fs = require("fs");
const os = require("os");
const path = require("path");
const Busboy = require("busboy");

const discordWebhookUrl = defineSecret("DISCORD_WEBHOOK_URL");
const discordPlanarStdWebhookUrl = defineSecret("DISCORD_WEBHOOK_PLANAR_STD");
const discordPauperWebhookUrl = defineSecret("DISCORD_WEBHOOK_PAUPER");

// Staging Discord webhooks — separate test channels so staging
// activity doesn't pollute production Discord notifications.
const discordWebhookStagingUrl = defineSecret("DISCORD_WEBHOOK_STAGING");
const discordPlanarStdStagingUrl = defineSecret("DISCORD_WEBHOOK_PLANAR_STD_STAGING");
const discordPauperStagingUrl = defineSecret("DISCORD_WEBHOOK_PAUPER_STAGING");

admin.initializeApp({
  databaseURL: "https://mtga-enhancement-suite-default-rtdb.firebaseio.com",
});

/**
 * Returns a database path scoped by environment.
 * env="staging" -> "staging/<path>", anything else -> "<path>".
 * All HTTP endpoints accept ?env=staging in the query string.
 * All onValueWritten triggers are registered twice (once per env).
 */
function scopePath(env, path) {
  const cleaned = String(path).replace(/^\/+/, "");
  return env === "staging" ? `staging/${cleaned}` : cleaned;
}

/**
 * Reads ?env=staging from a v2 onRequest event.
 */
function envFromReq(req) {
  const e = (req.query && req.query.env) || (req.body && req.body.env);
  return e === "staging" ? "staging" : "prod";
}

/**
 * Returns the right Discord secrets bag for the environment.
 */
function discordSecretsFor(env) {
  if (env === "staging") {
    return {
      general: discordWebhookStagingUrl.value() || null,
      planar:  discordPlanarStdStagingUrl.value() || null,
      pauper:  discordPauperStagingUrl.value() || null,
    };
  }
  return {
    general: discordWebhookUrl.value() || null,
    planar:  discordPlanarStdWebhookUrl.value() || null,
    pauper:  discordPauperWebhookUrl.value() || null,
  };
}

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
  standardpauper:     { displayName: "Standard Pauper",       scryfallQuery: '(game:arena) legal:standard', rawQuery: true, filterCommonPrints: true },
  planarstandard:     { displayName: "Planar Standard",       scryfallQuery: 'game:paper (set:ecl or set:eoe or set:tdm or set:dft or set:fdn or set:sos) -name:"Cori-steel Cutter"', rawQuery: true },
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
      const env = envFromReq(req);
      const result = await syncFormatFromScryfall(singleFormat, meta.scryfallQuery, meta.rawQuery, meta.filterCommonPrints, env);
      res.json(result);
    } else {
      const env = envFromReq(req);
      const results = await syncAllFormats(env);
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

async function syncAllFormats(env = "prod") {
  // Write the format list (clients read this to populate the spinner)
  const formatList = {};
  for (const [key, meta] of Object.entries(FORMAT_REGISTRY)) {
    formatList[key] = { displayName: meta.displayName };
  }
  for (const [key, meta] of Object.entries(EXTERNAL_FORMATS)) {
    formatList[key] = { displayName: meta.displayName };
  }
  await admin.database().ref(scopePath(env, "formatList")).set(formatList);
  console.log(`Format list written (env=${env}): ${Object.keys(formatList).join(", ")}`);

  // Sync each format's legal cards
  const results = [];
  for (const [key, meta] of Object.entries(FORMAT_REGISTRY)) {
    try {
      const result = await syncFormatFromScryfall(key, meta.scryfallQuery, meta.rawQuery, meta.filterCommonPrints, env);
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
async function syncFormatFromScryfall(format, scryfallQuery, rawQuery, filterCommonPrints, env = "prod") {
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

  // Compute unique card count (independent of prints vs cards mode)
  const uniqueCards = filterCommonPrints
    ? namesWithCommonPrint.size
    : Object.keys(legalNames).filter(n => !n.includes("%2F")).length;

  console.log(`Fetched ${totalFetched} ${filterCommonPrints ? "prints" : "cards"}, ${uniqueCards} unique cards, ${arenaIdCount} arena_ids, ${Object.keys(legalNames).length} name entries`);

  // Write to Firebase in one batch
  const update = {
    legalArenaIds,
    legalNames,
    lastSync: admin.database.ServerValue.TIMESTAMP,
    totalCards: uniqueCards,
    totalPrints: totalFetched,
    totalArenaIds: arenaIdCount,
    totalNames: Object.keys(legalNames).length,
  };

  await admin.database().ref(scopePath(env, `formats/${format}`)).set(update);

  const result = {
    format,
    totalCards: uniqueCards,
    totalPrints: totalFetched,
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

  const env = envFromReq(req);

  // Format can come from the request body directly, or looked up from the lobby
  let format = bodyFormat;
  if (!format && challengeId) {
    const lobbySnap = await admin
      .database()
      .ref(scopePath(env, `lobbies/${challengeId}`))
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
    admin.database().ref(scopePath(env, `formats/${format}/legalArenaIds`)).once("value"),
    admin.database().ref(scopePath(env, `formats/${format}/legalNames`)).once("value"),
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
  const env = envFromReq(req);
  const now = Math.floor(Date.now() / 1000);
  const staleThreshold = now - 120; // 2 minutes

  const lobbiesSnap = await admin.database().ref(scopePath(env, "lobbies")).once("value");
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

  const env = envFromReq(req);

  // Check lobby exists and is open
  const lobbySnap = await admin.database().ref(scopePath(env, `lobbies/${challengeId}`)).once("value");
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
  const existingSnap = await admin.database().ref(scopePath(env, `lobbies/${challengeId}/joinRequests`))
    .orderByChild("username").equalTo(username).once("value");

  if (existingSnap.exists()) {
    const existing = existingSnap.val();
    for (const [key, jr] of Object.entries(existing)) {
      if (jr.status === "pending") {
        res.json({ success: true, requestId: key, message: "Request already pending" });
        return;
      }
    }
  }

  // Write join request
  const ref = admin.database().ref(scopePath(env, `lobbies/${challengeId}/joinRequests`)).push();
  await ref.set({
    username: username,
    timestamp: admin.database.ServerValue.TIMESTAMP,
    status: "pending",
  });

  console.log(`Join request (env=${env}): ${username} → lobby ${challengeId} (${ref.key})`);
  res.json({ success: true, requestId: ref.key });
});

/**
 * Expires Discord messages for lobbies with stale heartbeats.
 * Runs every 2 minutes. Does NOT delete the lobby itself — just edits
 * the Discord messages to "Lobby Closed" so they match the in-game
 * browser's 2-minute staleness filter.
 */
exports.expireStaleDiscordMessages = onScheduleV2(
  {
    schedule: "*/2 * * * *",
    timeZone: "UTC",
    secrets: [
      discordWebhookUrl, discordPlanarStdWebhookUrl, discordPauperWebhookUrl,
      discordWebhookStagingUrl, discordPlanarStdStagingUrl, discordPauperStagingUrl,
    ],
  },
  async (event) => {
    for (const env of ["prod", "staging"]) {
      try {
        await expireStaleForEnv(env);
      } catch (err) {
        console.error(`expireStaleDiscordMessages failed for ${env}: ${err.message}`);
      }
    }
  }
);

async function expireStaleForEnv(env) {
  const now = Math.floor(Date.now() / 1000);
  const staleThreshold = now - 120; // 2 minutes — matches in-game browser filter

  const [lobbiesSnap, msgsSnap] = await Promise.all([
    admin.database().ref(scopePath(env, "lobbies")).once("value"),
    admin.database().ref(scopePath(env, "discordMessages")).once("value"),
  ]);

  const lobbies = lobbiesSnap.exists() ? lobbiesSnap.val() : {};
  const tracked = msgsSnap.exists() ? msgsSnap.val() : {};
  const secrets = discordSecretsFor(env);

  const allIds = new Set([...Object.keys(lobbies), ...Object.keys(tracked)]);

  for (const id of allIds) {
    const lobby = lobbies[id];
    const hasTrackedMessages = !!tracked[id];
    const hasLegacyMessages = lobby && lobby.discordMessages;
    if (!hasTrackedMessages && !hasLegacyMessages) continue;

    if (!lobby) {
      console.log(`[${env}] Expiring orphaned Discord messages for missing lobby ${id}`);
      try {
        await expireDiscordMessages(id, {}, secrets, env);
      } catch (err) {
        console.error(`[${env}] Failed to expire orphaned messages for ${id}: ${err.message}`);
      }
      continue;
    }

    const lastHeartbeat = lobby.lastHeartbeat || lobby.createdAt || 0;
    if (lastHeartbeat >= staleThreshold) continue;

    console.log(`[${env}] Expiring Discord messages for stale lobby ${id} (heartbeat ${now - lastHeartbeat}s ago)`);
    try {
      await expireDiscordMessages(id, lobby, secrets, env);
    } catch (err) {
      console.error(`[${env}] Failed to expire messages for ${id}: ${err.message}`);
    }
  }
}

/**
 * Cleans up stale lobbies every 30 minutes.
 * Deletes any lobby with lastHeartbeat older than 5 minutes.
 * Discord messages are already handled by expireStaleDiscordMessages.
 */
exports.cleanStaleLobbies = onScheduleV2(
  { schedule: "*/30 * * * *", timeZone: "UTC" },
  async (event) => {
    for (const env of ["prod", "staging"]) {
      try {
        await cleanStaleForEnv(env);
      } catch (err) {
        console.error(`cleanStaleLobbies failed for ${env}: ${err.message}`);
      }
    }
  }
);

async function cleanStaleForEnv(env) {
  const now = Math.floor(Date.now() / 1000);
  const staleThreshold = now - 300; // 5 minutes

  const lobbiesSnap = await admin.database().ref(scopePath(env, "lobbies")).once("value");
  if (!lobbiesSnap.exists()) return;

  const lobbies = lobbiesSnap.val();
  const deletions = [];

  for (const [id, lobby] of Object.entries(lobbies)) {
    const lastHeartbeat = lobby.lastHeartbeat || lobby.createdAt || 0;
    if (lastHeartbeat < staleThreshold) {
      deletions.push(admin.database().ref(scopePath(env, `lobbies/${id}`)).remove());
      console.log(`[${env}] Deleting stale lobby ${id} (last heartbeat: ${lastHeartbeat})`);
    }
  }

  if (deletions.length > 0) {
    await Promise.all(deletions);
    console.log(`[${env}] Cleaned up ${deletions.length} stale lobbies`);
  }
}

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
// Discord message IDs are stored at /discordMessages/{lobbyId}/{channel}/{embed,username}
// (NOT under /lobbies/{lobbyId}/discordMessages) so they survive PUT overwrites of the
// lobby node when the host clicks "Make Public" multiple times or re-registers.
async function storeMessageIds(lobbyId, channel, embedMsgId, usernameMsgId, env = "prod") {
  const updates = {};
  const base = scopePath(env, `discordMessages/${lobbyId}/${channel}`);
  if (embedMsgId) updates[`${base}/embed`] = embedMsgId;
  if (usernameMsgId) updates[`${base}/username`] = usernameMsgId;
  if (Object.keys(updates).length > 0) {
    await admin.database().ref().update(updates);
  }
}

// Read tracked Discord message IDs for a lobby. Checks the new path first,
// then falls back to the legacy /lobbies/{id}/discordMessages path.
async function getMessageIds(lobbyId, lobbyData, env = "prod") {
  // Try new path
  const snap = await admin.database().ref(scopePath(env, `discordMessages/${lobbyId}`)).once("value");
  if (snap.exists()) return snap.val();
  // Legacy fallback
  return lobbyData ? lobbyData.discordMessages : null;
}

/**
 * Edits all tracked Discord messages for a lobby to show "Lobby Closed".
 * Reads message IDs from /discordMessages/{lobbyId} (with legacy fallback).
 */
async function expireDiscordMessages(lobbyId, lobby, secrets, env = "prod") {
  const messages = await getMessageIds(lobbyId, lobby, env);
  if (!messages) return;

  const host = (lobby && lobby.hostDisplayName) || "Unknown";
  const format = (lobby && lobby.format) || "none";

  const webhookMap = {
    general: secrets.general,
    planar: secrets.planar,
    pauper: secrets.pauper,
  };

  // Per-channel format display names match the original webhook send logic
  function formatDisplayFor(channel) {
    if (format === "none") return "No Format";
    if (channel === "planar") return "Planar Standard";
    if (channel === "pauper") {
      // pauper webhook displays "pauper" as "Vintage Pauper"
      return PAUPER_DISPLAY_NAMES[format] || FORMAT_REGISTRY[format]?.displayName || format;
    }
    // general channel: use registry display name
    return FORMAT_REGISTRY[format]?.displayName ||
           PAUPER_DISPLAY_NAMES[format] ||
           format.charAt(0).toUpperCase() + format.slice(1);
  }

  for (const [channel, msgIds] of Object.entries(messages)) {
    const webhookUrl = webhookMap[channel];
    if (!webhookUrl) continue;

    const formatDisplay = formatDisplayFor(channel);

    try {
      // Edit the embed message to show closed
      if (msgIds.embed) {
        await editWebhookMessage(webhookUrl, msgIds.embed, {
          embeds: [{
            title: "🚫 Lobby Closed",
            color: 0x666666,
            description: `~~${host}'s ${formatDisplay} lobby~~ — This lobby is no longer available.\n\n[Learn more about MTGA+](https://github.com/MayerDaniel/MTGA-Enhancement-Suite)`,
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

  // Clean up tracked message IDs (both new path and legacy)
  try {
    await admin.database().ref(scopePath(env, `discordMessages/${lobbyId}`)).remove();
    console.log(`Cleaned up /${scopePath(env, `discordMessages/${lobbyId}`)}`);
  } catch (err) {
    console.log(`Could not clean /${scopePath(env, `discordMessages/${lobbyId}`)}: ${err.message}`);
  }
  try {
    const legacyRef = admin.database().ref(scopePath(env, `lobbies/${lobbyId}/discordMessages`));
    const legacySnap = await legacyRef.once("value");
    if (legacySnap.exists()) await legacyRef.remove();
  } catch (err) {
    // Lobby may already be deleted, that's fine
  }
}

/**
 * Sends a Discord notification when a lobby becomes public.
 * Tracks message IDs for later editing when the lobby closes.
 * Registered twice — once for /lobbies/{id} (prod) and once for /staging/lobbies/{id}.
 */
async function handleNotifyGeneral(event, env) {
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

  const webhookUrl = discordSecretsFor(env).general;
  if (!webhookUrl) {
    console.log(`[${env}] notifyDiscordOnPublicLobby: no webhook configured, skipping`);
    return;
  }

  try {
    const embedMsgId = await sendWebhookMessage(webhookUrl, {
      embeds: [{
        title: env === "staging" ? "🧪 [STAGING] New Public Lobby" : "🎮 New Public Lobby",
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

    await storeMessageIds(lobbyId, "general", embedMsgId, usernameMsgId, env);
    console.log(`[${env}] Discord notified: ${host} hosting ${formatDisplay} (${lobbyId}), msgIds: ${embedMsgId}, ${usernameMsgId}`);
  } catch (err) {
    console.error(`[${env}] Discord webhook error: ${err.message}`);
  }
}

exports.notifyDiscordOnPublicLobby = onValueWritten(
  {
    ref: "/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordWebhookUrl],
  },
  (event) => handleNotifyGeneral(event, "prod")
);

exports.notifyDiscordOnPublicLobbyStaging = onValueWritten(
  {
    ref: "/staging/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordWebhookStagingUrl],
  },
  (event) => handleNotifyGeneral(event, "staging")
);

/**
 * Sends a Discord notification to the Planar Standard channel.
 * Registered twice — once per env.
 */
async function handleNotifyPlanarStd(event, env) {
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

  const webhookUrl = discordSecretsFor(env).planar;
  if (!webhookUrl) {
    console.log(`[${env}] notifyDiscordPlanarStandard: no webhook configured, skipping`);
    return;
  }

  try {
    const embedMsgId = await sendWebhookMessage(webhookUrl, {
      embeds: [{
        title: env === "staging" ? "🧪 [STAGING] Planar Standard Lobby Open" : "⚔️ Planar Standard Lobby Open",
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

    // Skip the @LFG-Arena role ping in staging
    const tagPrefix = env === "staging" ? "" : "<@&1429556399473557755> ";
    const usernameMsgId = await sendWebhookMessage(webhookUrl, {
      content: `${tagPrefix}${hostFull}`,
    });

    await storeMessageIds(lobbyId, "planar", embedMsgId, usernameMsgId, env);
    console.log(`[${env}] Planar Std Discord notified: ${host} (${lobbyId}), msgIds: ${embedMsgId}, ${usernameMsgId}`);
  } catch (err) {
    console.error(`[${env}] Planar Std Discord webhook error: ${err.message}`);
  }
}

exports.notifyDiscordPlanarStandard = onValueWritten(
  {
    ref: "/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordPlanarStdWebhookUrl],
  },
  (event) => handleNotifyPlanarStd(event, "prod")
);

exports.notifyDiscordPlanarStandardStaging = onValueWritten(
  {
    ref: "/staging/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordPlanarStdStagingUrl],
  },
  (event) => handleNotifyPlanarStd(event, "staging")
);

/**
 * Sends a Discord notification to the Pauper channel.
 * Registered twice — once per env.
 */
async function handleNotifyPauper(event, env) {
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

  const webhookUrl = discordSecretsFor(env).pauper;
  if (!webhookUrl) {
    console.log(`[${env}] notifyDiscordPauper: no webhook configured, skipping`);
    return;
  }

  try {
    const embedMsgId = await sendWebhookMessage(webhookUrl, {
      embeds: [{
        title: env === "staging" ? "🧪 [STAGING] Pauper Lobby Open" : "🃏 Pauper Lobby Open",
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

    await storeMessageIds(lobbyId, "pauper", embedMsgId, usernameMsgId, env);
    console.log(`[${env}] Pauper Discord notified: ${host} hosting ${formatDisplay} (${lobbyId}), msgIds: ${embedMsgId}, ${usernameMsgId}`);
  } catch (err) {
    console.error(`[${env}] Pauper Discord webhook error: ${err.message}`);
  }
}

exports.notifyDiscordPauper = onValueWritten(
  {
    ref: "/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordPauperWebhookUrl],
  },
  (event) => handleNotifyPauper(event, "prod")
);

exports.notifyDiscordPauperStaging = onValueWritten(
  {
    ref: "/staging/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordPauperStagingUrl],
  },
  (event) => handleNotifyPauper(event, "staging")
);

/**
 * Expires Discord messages when a lobby is deleted or set to private.
 * Registered for both prod and staging lobby paths.
 */
async function handleExpireOnClose(event, env) {
  const before = event.data.before.val();
  const after = event.data.after.val();
  const lobbyId = event.params.lobbyId;

  const lobbyDeleted = !after;
  const lobbyWentPrivate = after && !after.isPublic && (before && before.isPublic);

  if (!lobbyDeleted && !lobbyWentPrivate) return;

  const lobbyData = before || {};
  const afterData = after || {};
  const messages = await getMessageIds(lobbyId, lobbyData, env) ||
                   (afterData.discordMessages || null);
  if (!messages) return;

  console.log(`[${env}] Lobby ${lobbyId} closed (deleted=${lobbyDeleted}, wentPrivate=${lobbyWentPrivate}), expiring Discord messages for channels: ${Object.keys(messages).join(", ")}`);

  const displayLobby = { ...lobbyData, ...afterData };
  await expireDiscordMessages(lobbyId, displayLobby, discordSecretsFor(env), env);
}

exports.expireDiscordOnLobbyClose = onValueWritten(
  {
    ref: "/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordWebhookUrl, discordPlanarStdWebhookUrl, discordPauperWebhookUrl],
  },
  (event) => handleExpireOnClose(event, "prod")
);

exports.expireDiscordOnLobbyCloseStaging = onValueWritten(
  {
    ref: "/staging/lobbies/{lobbyId}",
    instance: "mtga-enhancement-suite-default-rtdb",
    secrets: [discordWebhookStagingUrl, discordPlanarStdStagingUrl, discordPauperStagingUrl],
  },
  (event) => handleExpireOnClose(event, "staging")
);

// ===========================================================================
// Card metadata pipeline
// The plugin uploads the MTGA SQLite card DB whenever the local hash differs
// from /cardMetadataVersion. We parse it here and write a normalized index
// that the website /gamemodes editor uses for the SQLite-style rarity/set
// selector. We don't store the .mtga file long-term — we just parse it
// in /tmp and discard.
// ===========================================================================

// Maps MTGA Rarity int -> human key used in game mode rules.
// 1=Basic Land, 2=Common, 3=Uncommon, 4=Rare, 5=Mythic.
const RARITY_NAMES = { 1: "basic", 2: "common", 3: "uncommon", 4: "rare", 5: "mythic" };

/**
 * Receives a multipart upload of the MTGA SQLite DB and writes a parsed
 * card-metadata index to Firebase. The plugin gates the call on a hash
 * comparison against /cardMetadataVersion to avoid redundant uploads.
 *
 * Multipart fields:
 *   hash   (text)   - 32-char hex hash from the MTGA filename
 *   db     (file)   - the .mtga SQLite file
 *
 * Query: ?env=staging routes to /staging/cardMetadata.
 */
exports.uploadCardDb = onRequest(
  { cors: true, memory: "1GiB", timeoutSeconds: 540 },
  async (req, res) => {
    if (req.method !== "POST") {
      res.status(405).json({ error: "Method not allowed" });
      return;
    }

    const env = envFromReq(req);

    // Optional auth: accept a Firebase ID token to identify the uploader.
    // Even unauth uploads are OK for now since the data is identical for everyone.
    let uploaderUid = null;
    const authHeader = req.headers.authorization || "";
    if (authHeader.startsWith("Bearer ")) {
      try {
        const decoded = await admin.auth().verifyIdToken(authHeader.substring(7));
        uploaderUid = decoded.uid;
      } catch (err) {
        // Ignore auth failure — proceed unauthenticated
      }
    }

    // Compare hash query param against current server hash before we even
    // accept the upload, so callers don't waste bandwidth.
    const incomingHash = (req.query.hash || req.body?.hash || "").toString().trim();
    if (!incomingHash || !/^[0-9a-fA-F]{8,64}$/.test(incomingHash)) {
      res.status(400).json({ error: "Missing or invalid hash query param" });
      return;
    }
    const versionRef = admin.database().ref(scopePath(env, "cardMetadataVersion"));
    const versionSnap = await versionRef.once("value");
    const currentVersion = versionSnap.exists() ? versionSnap.val() : null;
    if (currentVersion && currentVersion.hash === incomingHash) {
      res.json({ skipped: true, reason: "hash matches current version", currentVersion });
      return;
    }

    // Parse multipart upload to /tmp
    const tmpFile = await new Promise((resolve, reject) => {
      let outPath = null;
      let bb;
      try {
        bb = Busboy({ headers: req.headers, limits: { fileSize: 100 * 1024 * 1024 /* 100MB */ } });
      } catch (err) {
        reject(err);
        return;
      }
      bb.on("file", (fieldname, file, info) => {
        outPath = path.join(os.tmpdir(), `card-db-${Date.now()}.mtga`);
        const ws = fs.createWriteStream(outPath);
        file.pipe(ws);
        ws.on("close", () => { /* done */ });
      });
      bb.on("error", reject);
      bb.on("close", () => resolve(outPath));
      // Cloud Functions deliver the parsed body on req.rawBody; busboy needs the raw stream
      if (req.rawBody) {
        bb.end(req.rawBody);
      } else {
        req.pipe(bb);
      }
    });

    if (!tmpFile || !fs.existsSync(tmpFile)) {
      res.status(400).json({ error: "No file uploaded (expected multipart field 'db')" });
      return;
    }

    let parsed;
    try {
      parsed = parseCardDb(tmpFile);
    } catch (err) {
      console.error(`Failed to parse card DB: ${err.message}`);
      try { fs.unlinkSync(tmpFile); } catch {}
      res.status(400).json({ error: `Failed to parse SQLite file: ${err.message}` });
      return;
    }

    try {
      await admin.database().ref(scopePath(env, "cardMetadata")).set({
        sets: parsed.sets,
        rarities: ["basic", "common", "uncommon", "rare", "mythic"],
        cards: parsed.cards,
        totalCards: Object.keys(parsed.cards).length,
        totalSets: parsed.sets.length,
      });
      await versionRef.set({
        hash: incomingHash,
        uploadedBy: uploaderUid,
        uploadedAt: admin.database.ServerValue.TIMESTAMP,
        totalCards: Object.keys(parsed.cards).length,
      });
      console.log(`[${env}] uploadCardDb: parsed ${Object.keys(parsed.cards).length} cards across ${parsed.sets.length} sets, hash=${incomingHash}`);
      res.json({
        ok: true,
        env,
        totalCards: Object.keys(parsed.cards).length,
        totalSets: parsed.sets.length,
        hash: incomingHash,
      });
    } finally {
      try { fs.unlinkSync(tmpFile); } catch {}
    }
  }
);

/**
 * Opens the MTGA SQLite card DB and produces:
 *   - cards:  { grpId: { name, rarity, set, digitalSet, isToken, isPrimary, isRebalanced } }
 *   - sets:   [ { code, count } ] sorted by code
 * Skips tokens and basic lands' duplicate art entries — keeps primary cards.
 */
function parseCardDb(filePath) {
  const sqlite = require("better-sqlite3");
  const db = sqlite(filePath, { readonly: true, fileMustExist: true });
  try {
    // Build localized name lookup
    const enRows = db.prepare("SELECT LocId, Loc FROM Localizations_enUS").all();
    const enNames = {};
    for (const row of enRows) enNames[row.LocId] = row.Loc;

    const cardRows = db.prepare(`
      SELECT GrpId, TitleId, Rarity, ExpansionCode, DigitalReleaseSet,
             IsToken, IsPrimaryCard, IsRebalanced, Order_Title
      FROM Cards
    `).all();

    const cards = {};
    const setCounts = {};
    for (const row of cardRows) {
      if (row.IsToken) continue; // skip tokens
      const name = enNames[row.TitleId] || row.Order_Title || `Card_${row.GrpId}`;
      const rarity = RARITY_NAMES[row.Rarity] || "unknown";
      cards[String(row.GrpId)] = {
        name,
        rarity,
        set: row.ExpansionCode || "",
        digitalSet: row.DigitalReleaseSet || "",
        isPrimary: !!row.IsPrimaryCard,
        isRebalanced: !!row.IsRebalanced,
      };
      const setKey = row.ExpansionCode || "";
      if (setKey) setCounts[setKey] = (setCounts[setKey] || 0) + 1;
    }

    const sets = Object.entries(setCounts)
      .map(([code, count]) => ({ code, count }))
      .sort((a, b) => a.code.localeCompare(b.code));

    return { cards, sets };
  } finally {
    db.close();
  }
}

