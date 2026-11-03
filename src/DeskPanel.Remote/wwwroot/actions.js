'use strict';

// DeskPanel web: the Audio and Feeds screens and the navigation between the three sections. Loaded before app.js, which
// owns the shared helpers (api, showLogin, closePreview) and calls setupNav() once the user is signed in. Everything the
// screens do happens on the PC; the page only starts it and shows how it went. No inline script or markup from the
// server is ever trusted: text goes in with textContent, and the article preview lives in a sandboxed frame.

const el = (id) => document.getElementById(id);
const VIEWS = ['Search', 'Audio', 'Feeds'];

// ---------- navigation ----------
function setupNav(actionsEnabled) {
  for (const tab of document.querySelectorAll('.navtab[data-view]')) {
    if (tab.dataset.view !== 'Search') { tab.hidden = !actionsEnabled; }
  }
  showView('Search');
}

function showView(name) {
  if (name !== 'Search') { closePreview(); }
  if (name !== 'Feeds') { closeFeedPreview(); }

  for (const view of VIEWS) { el(`view${view}`).hidden = view !== name; }
  for (const tab of document.querySelectorAll('.navtab[data-view]')) {
    if (tab.dataset.view === name) { tab.setAttribute('aria-current', 'page'); } else { tab.removeAttribute('aria-current'); }
  }

  if (name === 'Audio') { startAudioPolling(); } else { stopAudioPolling(); }
  if (name === 'Feeds' && !feeds.loaded) { loadFeeds(false); }
}

for (const tab of document.querySelectorAll('.navtab[data-view]')) {
  tab.addEventListener('click', () => showView(tab.dataset.view));
}

function formatSize(bytes) {
  return bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

function formatDate(value) {
  return value ? new Date(value).toLocaleString('es', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '';
}

// ---------- audio ----------
const audio = { timer: 0, token: 0, unticked: new Set(), signature: '', files: [], running: false };

function startAudioPolling() {
  stopAudioPolling();
  const token = ++audio.token;
  const tick = async () => {
    await loadAudio();
    // Quick while something runs; slow otherwise, just to notice audios that arrive in the folder.
    if (token === audio.token) { audio.timer = setTimeout(tick, audio.running ? 2000 : 15000); }
  };
  tick();
}

function stopAudioPolling() {
  audio.token++;
  clearTimeout(audio.timer);
}

async function loadAudio() {
  try {
    renderAudio(await api('/api/actions/audio'));
  } catch (error) {
    el('audioStatus').textContent = error.message;
  }
}

function renderAudio(state) {
  audio.running = state.running;
  audio.files = state.files;

  const warning = el('audioWarning');
  warning.hidden = !state.warning;
  warning.textContent = state.warning || '';
  el('audioStatus').textContent = state.status || '';

  // Rebuild the list only when it changed, so a poll does not steal the finger from a checkbox.
  const signature = `${state.running}|${state.files.map((f) => f.name).join('\n')}`;
  if (signature !== audio.signature) {
    audio.signature = signature;
    const list = el('audioList');
    list.replaceChildren();
    for (const file of state.files) {
      const label = document.createElement('label');
      const box = document.createElement('input');
      box.type = 'checkbox';
      box.dataset.name = file.name;
      box.checked = !audio.unticked.has(file.name);
      box.disabled = state.running;
      box.addEventListener('change', () => { if (box.checked) { audio.unticked.delete(file.name); } else { audio.unticked.add(file.name); } });
      const name = document.createElement('span');
      name.className = 'name';
      name.textContent = file.name;
      const meta = document.createElement('span');
      meta.className = 'meta';
      meta.textContent = `${formatSize(file.size)} · ${formatDate(file.modifiedUtc)}`;
      label.append(box, name, meta);
      list.append(label);
    }
  }

  el('audioEmpty').hidden = state.files.length > 0;
  el('audioStart').disabled = state.running || state.files.length === 0;
  el('audioCancel').hidden = !state.running;

  const showRun = state.running || state.results.length > 0;
  el('audioRun').hidden = !state.running && !state.step;
  el('audioStep').textContent = state.step || '';
  el('audioProgress').value = state.progress || 0;

  const results = el('audioResults');
  results.replaceChildren();
  for (const result of state.results) {
    const item = document.createElement('li');
    item.className = result.ok ? 'ok' : 'fail';
    item.textContent = `${result.ok ? '✔' : '✘'} ${result.name}: ${result.message}`;
    results.append(item);
  }

  const logBox = el('audioLogBox');
  logBox.hidden = !(showRun && state.log.length);
  if (!logBox.hidden) {
    const pre = el('audioLog');
    const atEnd = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 8;
    pre.textContent = state.log.join('\n');
    if (atEnd) { pre.scrollTop = pre.scrollHeight; }
  }
}

el('audioRefresh').addEventListener('click', loadAudio);

el('audioToggleAll').addEventListener('click', () => {
  const boxes = [...el('audioList').querySelectorAll('input[type=checkbox]')];
  const allTicked = boxes.every((b) => b.checked);
  for (const box of boxes) {
    box.checked = !allTicked;
    if (box.checked) { audio.unticked.delete(box.dataset.name); } else { audio.unticked.add(box.dataset.name); }
  }
});

el('audioStart').addEventListener('click', async () => {
  const names = [...el('audioList').querySelectorAll('input[type=checkbox]:checked')].map((b) => b.dataset.name);
  if (names.length === 0) { el('audioStatus').textContent = 'Marca al menos un audio.'; return; }
  if (!confirm(`Se van a procesar ${names.length} audio(s). Si todo sale bien, los originales irán a la Papelera del equipo. ¿Empezar?`)) { return; }

  el('audioStart').disabled = true;
  try {
    await api('/api/actions/audio/start', { method: 'POST', json: { names } });
    audio.signature = '';
    await loadAudio();
    startAudioPolling();
  } catch (error) {
    el('audioStatus').textContent = error.message;
    el('audioStart').disabled = false;
  }
});

el('audioCancel').addEventListener('click', async () => {
  if (!confirm('¿Cancelar el proceso? El audio en curso se conserva en la carpeta de entrada.')) { return; }
  try { await api('/api/actions/audio/cancel', { method: 'POST' }); } catch (error) { el('audioStatus').textContent = error.message; }
  loadAudio();
});

// ---------- feeds ----------
const feeds = {
  loaded: false,
  entries: [],
  profile: null,
  active: null,       // the open entry
  previewOpen: false,
  busy: false,
  summaryFor: new Set(),   // ids that have a summary made in this visit
  summaries: new Map(),
  frameVersion: 0,
};

async function loadFeeds(refresh) {
  const status = el('feedsStatus');
  status.textContent = refresh ? 'Descargando los feeds…' : 'Cargando…';
  try {
    const params = new URLSearchParams({ refresh: String(refresh), read: String(el('feedsRead').checked) });
    const data = await api(`/api/actions/feeds?${params}`);
    feeds.loaded = true;
    feeds.entries = data.entries;

    const select = el('feedsProfile');
    if (select.options.length === 0 || refresh) {
      select.replaceChildren();
      for (const name of data.profiles) { select.append(new Option(name, name)); }
      select.value = feeds.profile && data.profiles.includes(feeds.profile) ? feeds.profile : data.defaultProfile;
    }
    feeds.profile = select.value;

    renderFeeds();
    const errors = data.errors.length ? ` Errores: ${data.errors.join(' | ')}` : '';
    const none = data.entries.length === 0 && !el('feedsRead').checked ? ' No hay entradas sin leer; marca «Leídos» para ver todas.' : '';
    status.textContent = `${data.entries.length} entrada(s).${none}${errors}`;
  } catch (error) {
    status.textContent = error.message;
  }
}

function renderFeeds() {
  const list = el('feedList');
  list.replaceChildren();
  for (const entry of feeds.entries) {
    const card = document.createElement('article');
    card.className = `hit${entry.isRead ? ' read' : ''}${feeds.active && feeds.active.id === entry.id ? ' active' : ''}`;
    card.tabIndex = 0;
    card.setAttribute('role', 'button');
    card.dataset.id = entry.id;

    const name = document.createElement('div');
    name.className = 'name';
    name.textContent = entry.title;
    if (entry.hasFullContent) {
      const badge = document.createElement('span');
      badge.className = 'badge';
      badge.textContent = 'completo';
      name.append(badge);
    }

    const meta = document.createElement('div');
    meta.className = 'feedmeta';
    meta.textContent = `${entry.source} · ${formatDate(entry.publishedUtc)}${entry.isRead ? ' · leído' : ''}`;

    card.append(name, meta);
    card.addEventListener('click', () => openFeed(entry));
    card.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openFeed(entry); } });
    list.append(card);
  }
}

function frameUrl(entry) {
  const params = new URLSearchParams({ id: entry.id, v: String(feeds.frameVersion) });
  if (feeds.profile) { params.set('profile', feeds.profile); }
  return `/api/actions/feeds/preview?${params}`;
}

function openFeed(entry) {
  const wasOpen = feeds.previewOpen;
  feeds.active = entry;
  feeds.previewOpen = true;
  feeds.frameVersion++;

  el('feedPreview').hidden = false;
  el('feedEmpty').hidden = true;
  el('feedTitle').textContent = entry.title;
  el('feedFrame').src = frameUrl(entry);
  setFeedNote('');
  showFeedArticle();
  updateFeedButtons();
  for (const card of el('feedList').children) { card.classList.toggle('active', card.dataset.id === entry.id); }

  // So the phone's Back button closes the preview instead of leaving the page.
  if (!wasOpen) { history.pushState({ feedPreview: true }, ''); }
}

function closeFeedPreview() {
  if (!feeds.previewOpen) { return; }
  feeds.previewOpen = false;
  el('feedPreview').hidden = true;
  el('feedEmpty').hidden = false;
  el('feedFrame').src = 'about:blank';
  for (const card of el('feedList').children) { card.classList.remove('active'); }
}

function setFeedNote(text, bad = false) {
  const note = el('feedNote');
  note.hidden = !text;
  note.textContent = text;
  note.style.color = bad ? 'var(--danger)' : '';
}

function updateFeedButtons() {
  const entry = feeds.active;
  const hasSummary = !!entry && feeds.summaries.has(entry.id);
  el('feedFull').textContent = entry && entry.hasFullContent ? 'Completo ✓' : 'Completo';
  el('feedFull').disabled = feeds.busy || !entry || !entry.hasLink;
  el('feedSummarize').disabled = feeds.busy || !entry;
  el('feedPdf').disabled = feeds.busy || !entry;
  el('feedSummaryTabs').hidden = !hasSummary;
}

function showFeedArticle() {
  el('feedFrame').hidden = false;
  el('feedSummaryText').hidden = true;
  el('tabArticle').setAttribute('aria-pressed', 'true');
  el('tabSummary').setAttribute('aria-pressed', 'false');
}

function showFeedSummary() {
  const entry = feeds.active;
  if (!entry || !feeds.summaries.has(entry.id)) { return; }
  el('feedSummaryText').textContent = feeds.summaries.get(entry.id);
  el('feedFrame').hidden = true;
  el('feedSummaryText').hidden = false;
  el('tabArticle').setAttribute('aria-pressed', 'false');
  el('tabSummary').setAttribute('aria-pressed', 'true');
}

// Runs one of the slow actions: buttons off while it works, the outcome shown in the note.
async function feedAction(progressText, work) {
  const entry = feeds.active;
  if (!entry || feeds.busy) { return; }
  feeds.busy = true;
  updateFeedButtons();
  setFeedNote(progressText);
  try {
    await work(entry);
  } catch (error) {
    setFeedNote(error.message, true);
  } finally {
    feeds.busy = false;
    updateFeedButtons();
  }
}

el('feedFull').addEventListener('click', () => feedAction('Descargando el artículo completo…', async (entry) => {
  const result = await api('/api/actions/feeds/full', { method: 'POST', json: { id: entry.id } });
  setFeedNote(result.message, !result.ok);
  if (result.ok) {
    entry.hasFullContent = !entry.hasFullContent;
    feeds.summaries.delete(entry.id);
    feeds.frameVersion++;
    el('feedFrame').src = frameUrl(entry);
    showFeedArticle();
    renderFeeds();
  }
}));

el('feedSummarize').addEventListener('click', () => feedAction('Generando el resumen con la IA… (puede tardar un poco)', async (entry) => {
  const result = await api('/api/actions/feeds/summary', { method: 'POST', json: { id: entry.id } });
  if (result.ok) {
    feeds.summaries.set(entry.id, result.markdown);
    setFeedNote(result.message);
    showFeedSummary();
  } else {
    setFeedNote(result.message, true);
  }
}));

el('feedPdf').addEventListener('click', () => feedAction('Generando el PDF…', async (entry) => {
  const result = await api('/api/actions/feeds/pdf', { method: 'POST', json: { id: entry.id, profile: feeds.profile } });
  setFeedNote(result.message, !result.ok);
  if (result.ok) {
    entry.isRead = true;
    if (!el('feedsRead').checked) { feeds.entries = feeds.entries.filter((e) => e.id !== entry.id); }
    renderFeeds();
  }
}));

el('feedSaveSummary').addEventListener('click', () => feedAction('Guardando el resumen…', async (entry) => {
  const result = await api('/api/actions/feeds/summary/save', { method: 'POST', json: { id: entry.id } });
  setFeedNote(result.message, !result.ok);
}));

el('tabArticle').addEventListener('click', showFeedArticle);
el('tabSummary').addEventListener('click', showFeedSummary);
el('feedsRefresh').addEventListener('click', () => loadFeeds(true));
el('feedsRead').addEventListener('change', () => loadFeeds(false));
el('feedsProfile').addEventListener('change', () => {
  feeds.profile = el('feedsProfile').value;
  if (feeds.active && feeds.previewOpen) {
    feeds.frameVersion++;
    el('feedFrame').src = frameUrl(feeds.active);
  }
});

el('feedBack').addEventListener('click', () => {
  if (history.state && history.state.feedPreview) { history.back(); } else { closeFeedPreview(); }
});
window.addEventListener('popstate', closeFeedPreview);
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && feeds.previewOpen) {
    if (history.state && history.state.feedPreview) { history.back(); } else { closeFeedPreview(); }
  }
});
