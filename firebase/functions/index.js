const { onRequest } = require("firebase-functions/v2/https");
const { onSchedule: onScheduleV2 } = require("firebase-functions/v2/scheduler");
const { onValueWritten } = require("firebase-functions/v2/database");
const { defineSecret } = require("firebase-functions/params");
const admin = require("firebase-admin");

const discordWebhookUrl = defineSecret("DISCORD_WEBHOOK_URL");
const discordPlanarStdWebhookUrl = defineSecret("DISCORD_WEBHOOK_PLANAR_STD");

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
  historicpauper:     { displayName: "Historic Pauper",       scryfallQuery: '(game:arena) legal:historic rarity:c -"ancestral mask" -"cranial ram" -"galvanic blast" -"persistent petitioners" -"refurbished familiar" -"sneaky snacker" -"kuldotha rebirth"', rawQuery: true },
  standardpauper:     { displayName: "Standard Pauper",       scryfallQuery: '(game:arena) legal:standard (set:tmt OR set:ecl OR set:tla OR set:spm OR set:om1 OR set:eoe OR set:fin OR set:tdm OR set:dft OR set:dsk OR set:blb OR set:otj OR set:mkm OR set:lci OR set:woe OR set:fdn) rarity:c prefer:best', rawQuery: true },
  planarstandard:     { displayName: "Planar Standard",       scryfallQuery: 'game:paper (set:ecl or set:eoe or set:tdm or set:dft or set:fdn) -name:"Cori-steel Cutter"', rawQuery: true },
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
      if (!FORMAT_REGISTRY[singleFormat]) {
        res.status(400).json({ error: `Unknown format: ${singleFormat}`, available: Object.keys(FORMAT_REGISTRY) });
        return;
      }
      const meta = FORMAT_REGISTRY[singleFormat];
      const result = await syncFormatFromScryfall(singleFormat, meta.scryfallQuery, meta.rawQuery);
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
async function syncAllFormats() {
  // Write the format list (clients read this to populate the spinner)
  const formatList = {};
  for (const [key, meta] of Object.entries(FORMAT_REGISTRY)) {
    formatList[key] = { displayName: meta.displayName };
  }
  await admin.database().ref("formatList").set(formatList);
  console.log(`Format list written: ${Object.keys(formatList).join(", ")}`);

  // Sync each format's legal cards
  const results = [];
  for (const [key, meta] of Object.entries(FORMAT_REGISTRY)) {
    try {
      const result = await syncFormatFromScryfall(key, meta.scryfallQuery, meta.rawQuery);
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
async function syncFormatFromScryfall(format, scryfallQuery, rawQuery) {
  const query = scryfallQuery || `legal:${format}`;
  // rawQuery means the query already includes game: filter — don't append game:arena
  const fullQuery = rawQuery ? query : `${query} game:arena`;
  console.log(`Starting ${format} sync from Scryfall (query: ${fullQuery})...`);

  // Store by card NAME (lowercase) for printing-agnostic checks.
  // A card is legal if ANY printing is legal in the format.
  const legalCards = {}; // lowercase card name -> true
  let page = 1;
  let hasMore = true;
  let totalFetched = 0;

  while (hasMore) {
    const url = `${SCRYFALL_SEARCH_URL}?q=${encodeURIComponent(fullQuery)}&unique=cards&format=json&page=${page}`;

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
      // Store the full name (e.g. "fire // ice")
      legalCards[encodeFirebaseKey(name)] = true;

      // Also store each individual face for DFCs/split cards
      // MTGA returns each face separately (just "Fire", not "Fire // Ice")
      if (name.includes(" // ")) {
        for (const face of name.split(" // ")) {
          legalCards[encodeFirebaseKey(face.trim())] = true;
        }
      }

      // Store printed_name too (paper equivalent for IP crossover cards like om1 Marvel set)
      // MTGA may use either the IP name or the paper name depending on user settings
      if (card.printed_name) {
        const printedName = card.printed_name.toLowerCase();
        legalCards[encodeFirebaseKey(printedName)] = true;
        if (printedName.includes(" // ")) {
          for (const face of printedName.split(" // ")) {
            legalCards[encodeFirebaseKey(face.trim())] = true;
          }
        }
      }

      // Also check card_faces for DFCs with separate printed_names
      if (card.card_faces) {
        for (const face of card.card_faces) {
          if (face.name) {
            legalCards[encodeFirebaseKey(face.name.toLowerCase())] = true;
          }
          if (face.printed_name) {
            legalCards[encodeFirebaseKey(face.printed_name.toLowerCase())] = true;
          }
        }
      }
    }

    totalFetched += data.data.length;
    hasMore = data.has_more;
    page++;

    // Respect Scryfall rate limits
    await sleep(SCRYFALL_DELAY_MS);
  }

  console.log(`Fetched ${totalFetched} cards, ${Object.keys(legalCards).length} with arena_ids`);

  // Write to Firebase in one batch
  const update = {
    legalCards,
    lastSync: admin.database.ServerValue.TIMESTAMP,
    totalCards: Object.keys(legalCards).length,
  };

  await admin.database().ref(`formats/${format}`).set(update);

  const result = {
    format,
    totalCards: Object.keys(legalCards).length,
    syncedAt: new Date().toISOString(),
  };

  console.log(`${format} sync complete:`, result);
  return result;
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
 * Looks up arena_ids against /formats/{format}/legalCards.
 */
exports.validateDeck = onRequest({ cors: true }, async (req, res) => {
  if (req.method !== "POST") {
    res.status(405).json({ error: "Method not allowed" });
    return;
  }

  const { challengeId, decklist } = req.body;

  if (!challengeId || !decklist || !Array.isArray(decklist)) {
    res.status(400).json({ error: "Missing challengeId or decklist" });
    return;
  }

  // Get the lobby to find its format
  const lobbySnap = await admin
    .database()
    .ref(`lobbies/${challengeId}`)
    .once("value");

  if (!lobbySnap.exists()) {
    res.status(404).json({ error: "Lobby not found" });
    return;
  }

  const lobby = lobbySnap.val();
  const format = lobby.format;

  if (!format || format === "none") {
    res.json({ valid: true });
    return;
  }

  // Get the legal cards list for this format
  const formatSnap = await admin
    .database()
    .ref(`formats/${format}/legalCards`)
    .once("value");

  if (!formatSnap.exists()) {
    res.status(500).json({
      error: `Legal cards list for ${format} not synced yet. Run syncPauperListHttp first.`,
    });
    return;
  }

  const legalCards = formatSnap.val(); // { arena_id: card_name, ... }

  const illegalCards = [];

  for (const card of decklist) {
    const arenaId = String(card.grpId);

    // Skip basic lands (rarity 1)
    if (card.rarityValue === 1) continue;

    if (!legalCards[arenaId]) {
      illegalCards.push({
        grpId: card.grpId,
        name: card.name,
        quantity: card.quantity,
        rarity: card.rarityName || "Unknown",
      });
    }
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
 * Cleans up stale lobbies every 30 minutes.
 * Deletes any lobby with lastHeartbeat older than 5 minutes.
 */
exports.cleanStaleLobbies = onScheduleV2(
  { schedule: "*/30 * * * *", timeZone: "UTC" },
  async (event) => {
    const now = Math.floor(Date.now() / 1000);
    const staleThreshold = now - 300; // 5 minutes

    const lobbiesSnap = await admin.database().ref("lobbies").once("value");
    if (!lobbiesSnap.exists()) return;

    const lobbies = lobbiesSnap.val();
    const deletions = [];

    for (const [id, lobby] of Object.entries(lobbies)) {
      const lastHeartbeat = lobby.lastHeartbeat || lobby.createdAt || 0;
      if (lastHeartbeat < staleThreshold) {
        deletions.push(admin.database().ref(`lobbies/${id}`).remove());
        console.log(`Deleting stale lobby ${id} (last heartbeat: ${lastHeartbeat})`);
      }
    }

    if (deletions.length > 0) {
      await Promise.all(deletions);
      console.log(`Cleaned up ${deletions.length} stale lobbies`);
    }
  }
);

/**
 * Sends a Discord notification when a lobby becomes public.
 * Triggers on any write to /lobbies/{lobbyId} — only notifies
 * when isPublic transitions from false/missing to true.
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

    // Only notify when isPublic transitions to true (not on every update)
    if (!after || !after.isPublic) return;
    if (before && before.isPublic) return; // already public, skip

    const lobby = after;
    const lobbyId = event.params.lobbyId;
    const host = lobby.hostDisplayName || "Unknown";
    const format = lobby.format || "none";
    const formatDisplay = format === "none" ? "No Format" : format.charAt(0).toUpperCase() + format.slice(1);
    const joinUrl = `https://mtga-enhancement-suite.web.app/join/${lobbyId}?format=${encodeURIComponent(format)}`;

    const webhookUrl = discordWebhookUrl.value();
    if (!webhookUrl) {
      console.error("DISCORD_WEBHOOK_URL secret not set");
      return;
    }

    try {
      const resp = await fetch(webhookUrl, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          embeds: [{
            title: "🎮 New Public Lobby",
            color: 0x00ff88,
            fields: [
              { name: "Host", value: host, inline: true },
              { name: "Format", value: formatDisplay, inline: true },
            ],
            url: joinUrl,
            description: `**[Click to join](${joinUrl})**`,
            timestamp: new Date().toISOString(),
          }],
        }),
      });

      if (!resp.ok) {
        console.error(`Discord webhook failed: ${resp.status} ${await resp.text()}`);
      } else {
        console.log(`Discord notified: ${host} hosting ${formatDisplay} (${lobbyId})`);
      }
    } catch (err) {
      console.error(`Discord webhook error: ${err.message}`);
    }
  }
);

/**
 * Sends a Discord notification to the Planar Standard channel
 * ONLY when a Planar Standard lobby becomes public.
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
    const joinUrl = `https://mtga-enhancement-suite.web.app/join/${lobbyId}?format=planarstandard`;

    const webhookUrl = discordPlanarStdWebhookUrl.value();
    if (!webhookUrl) {
      console.error("DISCORD_WEBHOOK_PLANAR_STD secret not set");
      return;
    }

    try {
      const resp = await fetch(webhookUrl, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          embeds: [{
            title: "⚔️ Planar Standard Lobby Open",
            color: 0xf5a623,
            fields: [
              { name: "Host", value: host, inline: true },
              { name: "Format", value: "Planar Standard", inline: true },
            ],
            url: joinUrl,
            description: `**[Click to join](${joinUrl})**`,
            timestamp: new Date().toISOString(),
          }],
        }),
      });

      if (!resp.ok) {
        console.error(`Planar Std Discord webhook failed: ${resp.status} ${await resp.text()}`);
      } else {
        console.log(`Planar Std Discord notified: ${host} (${lobbyId})`);
      }
    } catch (err) {
      console.error(`Planar Std Discord webhook error: ${err.message}`);
    }
  }
);
