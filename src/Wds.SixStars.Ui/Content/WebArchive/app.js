const STAR_META = {
  Elusiaa: { element: 'Light / Mercy', image: 'assets/stars/elusiaa.png' },
  Kaelia: { element: 'Void / Fatalism', image: 'assets/stars/kaelia.png' },
  Khavia: { element: 'Water / Friendship', image: 'assets/stars/khavia.png' },
  Auerlis: { element: 'Fire / Joy', image: 'assets/stars/auerlis.png' },
  Maera: { element: 'Blood / Possession', image: 'assets/stars/maera.png' },
  Nyxari: { element: 'Shadow / Fragmentation', image: 'assets/stars/nyxari.png' }
};
const CAT_IMAGE = 'assets/cat-avatar.png';
let archive = null;
let selectedStar = 'Elusiaa';
let parsedTurns = [];

const $ = (id) => document.getElementById(id);
const clamp = (n, min, max) => Math.max(min, Math.min(max, Number(n) || 0));
const safe = (v) => v == null ? '' : String(v);
const byDateAsc = (a,b) => new Date(a.createdAt || a.fileTime || 0) - new Date(b.createdAt || b.fileTime || 0);
const byDateDesc = (a,b) => new Date(b.createdAt || b.fileTime || 0) - new Date(a.createdAt || a.fileTime || 0);

function normalizeStarId(value) {
  const text = safe(value).trim();
  return Object.keys(STAR_META).find(x => x.toLowerCase() === text.toLowerCase()) || text || 'Elusiaa';
}

function top3(emotions) {
  return (Array.isArray(emotions) ? emotions : [])
    .slice()
    .sort((a,b) => Math.abs(Number(b.weightedScore ?? b.score ?? 0)) - Math.abs(Number(a.weightedScore ?? a.score ?? 0)))
    .slice(0,3);
}

function emotionChip(e) {
  const emotion = safe(e.emotionId || e.EmotionId || 'emotion');
  const valence = safe(e.valence || e.Valence).toLowerCase();
  const score = Number(e.weightedScore ?? e.WeightedScore ?? e.score ?? e.Score ?? 0);
  const conf = Number(e.confidence ?? e.Confidence ?? 0);
  const cls = valence === 'negative' ? 'chip negative' : 'chip';
  return `<span class="${cls}" title="confidence ${conf.toFixed(2)}">${escapeHtml(emotion)} ${score >= 0 ? '+' : ''}${score.toFixed(1)}</span>`;
}

function escapeHtml(str) {
  return safe(str).replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
}

function valueOf(obj, ...names) {
  for (const name of names) {
    if (obj && Object.prototype.hasOwnProperty.call(obj, name)) return obj[name];
    const alt = name.charAt(0).toUpperCase() + name.slice(1);
    if (obj && Object.prototype.hasOwnProperty.call(obj, alt)) return obj[alt];
  }
  return undefined;
}

function parseJsonBlocks(markdown) {
  const blocks = [];
  const regex = /```json\s*([\s\S]*?)```/g;
  let match;
  while ((match = regex.exec(markdown || ''))) {
    try { blocks.push(JSON.parse(match[1])); } catch { }
  }
  return blocks;
}

function turnKey(turn) {
  const date = turn.createdAt ? new Date(turn.createdAt) : null;
  const minute = date && !Number.isNaN(date.getTime()) ? date.toISOString().slice(0,16) : safe(turn.fileTime);
  return [normalizeStarId(turn.starId), minute, safe(turn.playerText).trim(), safe(turn.starText).trim()].join('|');
}

function mergeTurns(turnCollections) {
  const merged = [];
  const seen = new Set();
  for (const collection of turnCollections || []) {
    for (const turn of collection || []) {
      if (!safe(turn.playerText).trim() && !safe(turn.starText).trim()) continue;
      const key = turnKey(turn);
      if (seen.has(key)) continue;
      seen.add(key);
      merged.push(turn);
    }
  }
  return merged.sort(byDateAsc);
}

function conversationTurnFromEntry(entry, starId, source, archiveEntry) {
  const playerText = valueOf(entry, 'playerText') || '';
  const starText = valueOf(entry, 'starResponse') || valueOf(entry, 'starText') || '';
  if (!safe(playerText).trim() && !safe(starText).trim()) return null;
  return {
    kind: valueOf(entry, 'kind') || 'saved-conversation-turn',
    source: source || valueOf(entry, 'source') || '',
    starId: normalizeStarId(starId || valueOf(entry, 'starId')),
    archiveId: valueOf(entry, 'archiveId') || valueOf(archiveEntry || {}, 'id') || '',
    archiveSummary: valueOf(entry, 'archiveSummary') || valueOf(archiveEntry || {}, 'summary') || '',
    archiveStartedAt: valueOf(entry, 'archiveStartedAt') || valueOf(archiveEntry || {}, 'startedAt'),
    archiveEndedAt: valueOf(entry, 'archiveEndedAt') || valueOf(archiveEntry || {}, 'endedAt'),
    createdAt: valueOf(entry, 'createdAt') || valueOf(archiveEntry || {}, 'endedAt'),
    requestType: valueOf(entry, 'requestType') || '',
    playerText,
    starText,
    emotions: valueOf(entry, 'topAffectedEmotions') || [],
    combos: valueOf(entry, 'acceptedCombos') || [],
    positiveSignals: valueOf(entry, 'acceptedPositiveSignals') || [],
    negativeSignals: valueOf(entry, 'acceptedNegativeSignals') || [],
    actualDelta: valueOf(entry, 'actualDelta') || {},
    suggestedDelta: valueOf(entry, 'suggestedDelta') || {},
    summary: valueOf(entry, 'summary') || valueOf(entry, 'deterministicEmotionSummary') || valueOf(entry, 'archiveSummary') || valueOf(archiveEntry || {}, 'summary') || '',
    tags: valueOf(entry, 'tags') || valueOf(entry, 'archiveTags') || valueOf(archiveEntry || {}, 'topicTags') || [],
    relationshipEvents: valueOf(entry, 'relationshipEvents') || []
  };
}

function parseArchiveConversationTurns(turns) {
  return (turns || [])
    .map(t => conversationTurnFromEntry(t, valueOf(t, 'starId'), valueOf(t, 'source'), null))
    .filter(Boolean)
    .sort(byDateAsc);
}

function parseSaveDataConversationTurns() {
  const turns = [];
  for (const state of getStates()) {
    const starId = normalizeStarId(valueOf(state, 'starId'));
    for (const archiveEntry of valueOf(state, 'oldConversationArchives') || []) {
      for (const entry of valueOf(archiveEntry, 'entries') || []) {
        const turn = conversationTurnFromEntry(entry, starId, 'old-archive', archiveEntry);
        if (turn) turns.push(turn);
      }
    }
    for (const entry of valueOf(state, 'memoryLog') || []) {
      const turn = conversationTurnFromEntry(entry, starId, 'memory-log', null);
      if (turn) turns.push(turn);
    }
  }
  return turns.sort(byDateAsc);
}

function parseLogs(logs) {
  const turns = [];
  for (const log of logs || []) {
    const fileTime = log.lastWriteTimeUtc || log.LastWriteTimeUtc;
    const blocks = parseJsonBlocks(log.markdown || log.Markdown || '');
    for (const block of blocks) {
      const kind = safe(valueOf(block, 'kind')).toLowerCase();
      if (kind === 'primary-player-turn') {
        const starId = normalizeStarId(valueOf(block, 'starId'));
        turns.push({
          kind,
          starId,
          fileName: log.fileName || log.FileName,
          fileTime,
          createdAt: valueOf(block, 'createdAt') || fileTime,
          requestType: valueOf(block, 'requestType'),
          playerText: valueOf(block, 'playerText'),
          starText: valueOf(block, 'starResponse'),
          emotions: valueOf(block, 'topAffectedEmotions') || [],
          combos: valueOf(block, 'acceptedCombos') || [],
          positiveSignals: valueOf(block, 'acceptedPositiveSignals') || [],
          negativeSignals: valueOf(block, 'acceptedNegativeSignals') || [],
          actualDelta: valueOf(block, 'actualDelta') || {},
          suggestedDelta: valueOf(block, 'suggestedDelta') || {},
          summary: valueOf(block, 'conversationSummary') || valueOf(block, 'deterministicSummary') || '',
          before: valueOf(block, 'sourceStateBefore') || {},
          after: valueOf(block, 'stateAfter') || {}
        });
      } else if (kind === 'interstar-destination-reaction') {
        const starId = normalizeStarId(valueOf(block, 'destinationStarId'));
        turns.push({
          kind,
          starId,
          sourceStarId: normalizeStarId(valueOf(block, 'sourceStarId')),
          fileName: log.fileName || log.FileName,
          fileTime,
          createdAt: valueOf(block, 'createdAt') || fileTime,
          playerText: `${valueOf(block, 'sourceStarId') || 'Another Star'} told ${starId}: ${valueOf(block, 'relayText') || valueOf(block, 'relaySummary') || ''}`,
          starText: valueOf(block, 'replyToSourceStar') || valueOf(block, 'conversationSummary') || '',
          emotions: valueOf(block, 'topAffectedEmotions') || [],
          combos: valueOf(block, 'acceptedCombos') || [],
          positiveSignals: valueOf(block, 'acceptedPositiveSignals') || [],
          negativeSignals: valueOf(block, 'acceptedNegativeSignals') || [],
          actualDelta: valueOf(block, 'actualDelta') || {},
          suggestedDelta: valueOf(block, 'suggestedDelta') || {},
          summary: valueOf(block, 'conversationSummary') || valueOf(block, 'changesSummary') || '',
          before: valueOf(block, 'destinationStateBefore') || {},
          after: valueOf(block, 'destinationStateAfter') || {}
        });
      } else if (kind === 'interstar-origin-feedback') {
        const starId = normalizeStarId(valueOf(block, 'originStarId'));
        turns.push({
          kind,
          starId,
          fileName: log.fileName || log.FileName,
          fileTime,
          createdAt: valueOf(block, 'createdAt') || fileTime,
          playerText: 'The other Stars reacted to what she shared.',
          starText: valueOf(block, 'conversationSummary') || valueOf(block, 'changesSummary') || '',
          emotions: valueOf(block, 'topAffectedEmotions') || [],
          combos: valueOf(block, 'acceptedCombos') || [],
          positiveSignals: valueOf(block, 'acceptedPositiveSignals') || [],
          negativeSignals: valueOf(block, 'acceptedNegativeSignals') || [],
          actualDelta: valueOf(block, 'actualDelta') || {},
          suggestedDelta: valueOf(block, 'suggestedDelta') || {},
          summary: valueOf(block, 'deterministicSummary') || '',
          before: valueOf(block, 'originStateBefore') || {},
          after: valueOf(block, 'originStateAfter') || {}
        });
      }
    }
  }
  return turns.sort(byDateAsc);
}

async function loadArchive() {
  $('loading').classList.remove('hidden');
  $('starPage').classList.add('hidden');
  $('searchPage').classList.add('hidden');
  const sessionId = $('sessionInput').value.trim() || 'android-phone-prod';
  const response = await fetch(`/api/archive/data?sessionId=${encodeURIComponent(sessionId)}&maxLogs=240`, { cache: 'no-store' });
  if (!response.ok) throw new Error(`Archive API returned ${response.status}`);
  archive = await response.json();
  if (archive.defaultSessionId && !$('sessionInput').dataset.touched) $('sessionInput').value = archive.sessionId || archive.defaultSessionId;
  const traceTurns = parseLogs(archive.logs || []);
  const savedTurns = parseArchiveConversationTurns(archive.conversationTurns || archive.conversations || []);
  const saveDataTurns = parseSaveDataConversationTurns();
  parsedTurns = mergeTurns([traceTurns, savedTurns, saveDataTurns]);
  selectedStar = normalizeStarId(archive.selectedStarId || selectedStar);
  renderStarStrip();
  renderWorldPulse();
  renderStarPage(selectedStar);
  $('loading').classList.add('hidden');
}

function getStates() {
  const saveData = archive?.saveData || archive?.SaveData || {};
  return saveData.stars || saveData.Stars || [];
}
function getState(starId) {
  return getStates().find(s => normalizeStarId(valueOf(s, 'starId')) === starId) || { starId };
}
function getDaily(starId) {
  const list = archive?.dailyStates || archive?.DailyStates || [];
  const wrapper = list.find(d => normalizeStarId(valueOf(d, 'starId')) === starId);
  return wrapper?.current || wrapper?.Current || null;
}

function renderStarStrip() {
  const strip = $('starStrip');
  strip.innerHTML = '';
  const ids = archive?.starIds || Object.keys(STAR_META);
  for (const raw of ids) {
    const starId = normalizeStarId(raw);
    const state = getState(starId);
    const meta = STAR_META[starId] || STAR_META.Elusiaa;
    const template = $('starButtonTemplate').content.cloneNode(true);
    const btn = template.querySelector('button');
    btn.classList.toggle('active', starId === selectedStar);
    btn.onclick = () => { selectedStar = starId; $('searchBox').value = ''; renderStarStrip(); renderStarPage(starId); };
    template.querySelector('img').src = meta.image;
    template.querySelector('.star-button-name').textContent = starId;
    const parts = [];
    if (valueOf(state, 'isGirlfriend')) parts.push('girlfriend');
    else if (valueOf(state, 'isGirlfriendReady')) parts.push('ready');
    if (valueOf(state, 'isAlly')) parts.push('ally');
    else if (valueOf(state, 'isMet')) parts.push('met');
    if (!parts.length) parts.push('mysterious');
    template.querySelector('.star-button-status').textContent = parts.join(' · ');
    strip.appendChild(template);
  }
}

function renderWorldPulse() {
  const states = getStates();
  const logs = archive?.logs || [];
  const ready = states.filter(s => valueOf(s,'isGirlfriendReady')).length;
  const gf = states.filter(s => valueOf(s,'isGirlfriend')).length;
  const allies = states.filter(s => valueOf(s,'isAlly')).length;
  const met = states.filter(s => valueOf(s,'isMet')).length;
  const saved = archive?.conversationTurns || archive?.conversations || [];
  $('worldPulse').innerHTML = `${escapeHtml(archive?.sessionId || '')}<br>${saved.length} saved turns · ${logs.length} trace files · ${parsedTurns.length} displayed moments<br>${allies} allies · ${met} met · ${ready} girlfriend-ready · ${gf} girlfriend`;
}

function renderStarPage(starId) {
  $('starPage').classList.remove('hidden');
  $('searchPage').classList.add('hidden');
  const state = getState(starId);
  const meta = STAR_META[starId] || STAR_META.Elusiaa;
  $('starName').textContent = starId;
  $('starElement').textContent = meta.element;
  $('starPortrait').src = meta.image;
  renderBadges(state);
  renderMeters(state);
  renderDaily(starId, state);
  renderEmotionTotals(state);
  renderRelationshipEvidence(state);
  renderGirlfriendState(state);
  renderConversation(starId);
}

function renderBadges(state) {
  const badges = [];
  if (valueOf(state,'isMet')) badges.push(`<span class="badge met">met</span>`); else badges.push(`<span class="badge met">unmet / mysterious</span>`);
  if (valueOf(state,'isAlly')) badges.push(`<span class="badge ally">ally</span>`); else badges.push(`<span class="badge">not ally yet</span>`);
  if (valueOf(state,'isGirlfriend')) badges.push(`<span class="badge gf">actual girlfriend</span>`);
  else if (valueOf(state,'isGirlfriendReady')) badges.push(`<span class="badge ready">girlfriend ready</span>`);
  else badges.push(`<span class="badge">romance locked</span>`);
  $('statusRow').innerHTML = badges.join('');
}

function meter(label, value, max, cssVar) {
  const n = clamp(value, 0, max);
  const pct = max <= 0 ? 0 : (n / max) * 100;
  return `<div class="meter"><div class="meter-label"><span>${label}</span><b>${Number(value || 0).toFixed(label === 'Connection' ? 1 : 0)}</b></div><div class="meter-bar"><div class="meter-fill" style="width:${pct}%; background:var(${cssVar});"></div></div></div>`;
}
function renderMeters(state) {
  $('meters').innerHTML = [
    meter('Heart', valueOf(state,'heart') || 0, 100, '--heart'),
    meter('Trust', valueOf(state,'trust') || 0, 100, '--trust'),
    meter('Romance', valueOf(state,'romance') || 0, 100, '--romance'),
    meter('Connection', valueOf(state,'emotionalConnectionPercent') || 0, 100, '--ally'),
    meter('Benaveil Influence', Math.abs(valueOf(state,'benaveilInfluence') || 0), 100, '--ready'),
    meter('Interactions', valueOf(state,'interactionCount') || 0, 100, '--gold')
  ].join('');
}

function dailyTextFromRecord(record, state) {
  const cur = record?.state || record?.State || record?.current || record?.Current || record;
  const candidates = [
    valueOf(cur, 'promptUseSummary'), valueOf(cur, 'summary'), valueOf(cur, 'feelingsSummary'), valueOf(cur, 'currentInnerLife'), valueOf(cur, 'todayActivity'),
    valueOf(state, 'todayState')?.promptUseSummary, valueOf(state, 'todayState')?.summary, valueOf(state, 'todayState')?.feelingsSummary
  ];
  for (const c of candidates) if (safe(c).trim()) return safe(c);
  try { return JSON.stringify(cur, null, 2).slice(0, 1500); } catch { return 'No daily state has been generated yet.'; }
}
function renderDaily(starId, state) {
  $('dailySummary').textContent = dailyTextFromRecord(getDaily(starId), state);
}

function renderEmotionTotals(state) {
  const positives = valueOf(state, 'positiveEmotionTotals') || [];
  const negatives = valueOf(state, 'negativeEmotionTotals') || [];
  const chips = [];
  positives.slice().sort((a,b)=>Number(valueOf(b,'score')||0)-Number(valueOf(a,'score')||0)).slice(0,8).forEach(e => chips.push(`<span class="chip">${escapeHtml(valueOf(e,'emotionId') || valueOf(e,'emotion') || 'emotion')} +${Number(valueOf(e,'score') || valueOf(e,'weightedScore') || 0).toFixed(1)}</span>`));
  negatives.slice().sort((a,b)=>Number(valueOf(b,'score')||0)-Number(valueOf(a,'score')||0)).slice(0,5).forEach(e => chips.push(`<span class="chip negative">${escapeHtml(valueOf(e,'emotionId') || valueOf(e,'emotion') || 'emotion')} -${Number(valueOf(e,'score') || valueOf(e,'weightedScore') || 0).toFixed(1)}</span>`));
  $('emotionTotals').innerHTML = chips.join('') || '<div class="empty">No emotion totals yet.</div>';
}

function renderRelationshipEvidence(state) {
  const acc = valueOf(state,'relationshipScoreAccumulators') || [];
  const rows = acc.map(a => `<div><b>${escapeHtml(valueOf(a,'stat') || '')}</b>: score ${Number(valueOf(a,'score')||0).toFixed(2)}, last +${Number(valueOf(a,'lastScoreAdded')||0).toFixed(2)} · +${valueOf(a,'totalPositivePoints')||0} / -${valueOf(a,'totalNegativePoints')||0}<br><span>${escapeHtml(valueOf(a,'lastReason') || '')}</span></div>`);
  const events = (valueOf(state,'relationshipEvents') || []).slice(0,6).map(e => `<div>✦ ${escapeHtml(e)}</div>`);
  $('relationshipEvidence').innerHTML = [...rows, ...events].join('') || '<div class="empty">No relationship evidence yet.</div>';
}

function renderGirlfriendState(state) {
  const lines = [];
  lines.push(`<b>Readiness:</b> ${escapeHtml(valueOf(state,'girlfriendReadinessSummary') || 'No readiness summary yet.')}`);
  const hourly = valueOf(state, 'girlfriendHourlyState') || {};
  if (valueOf(state,'isGirlfriend')) lines.push(`<b>Achievement:</b> actual girlfriend since ${escapeHtml(valueOf(state,'girlfriendSince') || 'unknown')}`);
  else if (valueOf(state,'isGirlfriendReady')) lines.push(`<b>Achievement:</b> girlfriend-ready since ${escapeHtml(valueOf(state,'girlfriendReadySince') || 'recently')}`);
  const wants = valueOf(hourly, 'wantVector') || valueOf(hourly, 'wants') || [];
  const needs = valueOf(hourly, 'needVector') || valueOf(hourly, 'needs') || [];
  if (Array.isArray(needs) && needs.length) lines.push(`<b>Current needs:</b> ${needs.slice(0,5).map(x=>escapeHtml(valueOf(x,'name') || valueOf(x,'id') || JSON.stringify(x))).join(', ')}`);
  if (Array.isArray(wants) && wants.length) lines.push(`<b>Current wants:</b> ${wants.slice(0,5).map(x=>escapeHtml(valueOf(x,'name') || valueOf(x,'id') || JSON.stringify(x))).join(', ')}`);
  $('girlfriendState').innerHTML = lines.join('<br>');
}

function renderConversation(starId) {
  const turns = parsedTurns.filter(t => normalizeStarId(t.starId) === starId).sort(byDateDesc);
  $('conversationTitle').textContent = `${starId} conversation`;
  $('conversationCount').textContent = `${turns.length} moments`;
  $('conversationList').innerHTML = turns.length ? turns.map(renderTurn).join('') : '<div class="empty">No parsed conversation trace for this Star yet. Send a message, wait for background review, then refresh.</div>';
}

function renderTurn(turn) {
  const meta = STAR_META[turn.starId] || STAR_META.Elusiaa;
  const date = turn.createdAt ? new Date(turn.createdAt).toLocaleString() : '';
  const deltas = renderDelta(turn.actualDelta);
  const combos = (turn.combos || []).map(c => `<span class="combo">combo: ${escapeHtml(c)}</span>`).join('');
  const emotions = top3(turn.emotions).map(emotionChip).join('');
  const signals = [...(turn.positiveSignals || []), ...(turn.negativeSignals || [])].slice(0,5).map(s => `<span class="chip">${escapeHtml(s)}</span>`).join('');
  const kindLabel = turn.kind === 'primary-player-turn' ? 'player turn' : turn.kind.replace(/-/g, ' ');
  return `<article class="turn-card">
    <div class="turn-head"><span>${escapeHtml(kindLabel)} · ${escapeHtml(turn.requestType || '')}</span><span>${escapeHtml(date)}</span></div>
    ${turn.playerText ? `<div class="message-row player"><div class="bubble">${escapeHtml(turn.playerText)}</div><img class="avatar" src="${CAT_IMAGE}" alt="The Last Cat"></div>` : ''}
    ${turn.starText ? `<div class="message-row star"><img class="avatar" src="${meta.image}" alt="${escapeHtml(turn.starId)}"><div class="bubble">${escapeHtml(turn.starText)}</div></div>` : ''}
    ${emotions ? `<div class="emotion-row">${emotions}</div>` : ''}
    ${deltas ? `<div class="delta-row">${deltas}</div>` : ''}
    ${combos ? `<div class="combo-row">${combos}</div>` : ''}
    ${signals ? `<div class="emotion-row">${signals}</div>` : ''}
    ${turn.summary ? `<div class="summary">${escapeHtml(turn.summary)}</div>` : ''}
  </article>`;
}

function renderDelta(delta) {
  const items = [];
  const map = { heart:'Heart', trust:'Trust', romance:'Romance', benaveilInfluence:'Benaveil', emotionalConnectionPercent:'Connection' };
  for (const [key,label] of Object.entries(map)) {
    const v = Number(valueOf(delta || {}, key) || 0);
    if (Math.abs(v) > 0.0001) items.push(`<span class="delta">${label} ${v > 0 ? '+' : ''}${key === 'emotionalConnectionPercent' ? v.toFixed(2) : v}</span>`);
  }
  return items.join('');
}

function runSearch(query) {
  const q = query.trim().toLowerCase();
  if (!q) { renderStarPage(selectedStar); return; }
  $('starPage').classList.add('hidden');
  $('searchPage').classList.remove('hidden');
  const results = parsedTurns.filter(t => [t.starId, t.playerText, t.starText, t.summary, ...(t.combos||[]), ...(t.positiveSignals||[]), ...(t.negativeSignals||[])].join('\n').toLowerCase().includes(q)).sort(byDateDesc);
  $('searchCount').textContent = `${results.length} results`;
  $('searchResults').innerHTML = results.length ? results.map(renderTurn).join('') : '<div class="empty">No matching conversations found.</div>';
}

$('refreshButton').addEventListener('click', () => loadArchive().catch(showError));
$('sessionInput').addEventListener('input', () => { $('sessionInput').dataset.touched = 'true'; });
$('searchBox').addEventListener('input', (e) => runSearch(e.target.value));
function showError(err) {
  $('loading').classList.remove('hidden');
  $('loading').innerHTML = `<b>Could not load archive.</b><br>${escapeHtml(err.message || err)}<br><br>Make sure the desktop host is running and open the page from the archive URL shown in the WPF app.`;
}
loadArchive().catch(showError);
