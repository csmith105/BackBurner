const byId = id => document.getElementById(id);
const ACTIVE_STATUSES = new Set(['leased', 'running', 'paused']);
const PRODUCTIVE_KINDS = new Set(['encodingcpu', 'encodinggpu', 'upscaling', 'draining']);
const state = {
  presets: [], identities: [], snapshot: null, scanFiles: [], scanDirectory: null,
  mode: 'single', requiresAuthentication: true,
  identityId: localStorage.getItem('backburner-identity-id') || ''
};

byId('admin-key').value = sessionStorage.getItem('backburner-admin-key') || '';
byId('connect').addEventListener('click', connect);
byId('admin-key').addEventListener('keydown', event => { if (event.key === 'Enter') connect(); });
byId('refresh-dashboard').addEventListener('click', refresh);
byId('refresh-workers').addEventListener('click', refresh);
byId('job-form').addEventListener('submit', queueWork);
byId('save-preset').addEventListener('click', savePreset);
byId('reset-settings').addEventListener('click', resetSettings);
byId('preset-picker').addEventListener('change', loadPreset);
byId('single-mode').addEventListener('click', () => setMode('single'));
byId('batch-mode').addEventListener('click', () => setMode('batch'));
byId('scan-directory').addEventListener('click', scanDirectory);
byId('select-all').addEventListener('click', () => setAllScanned(true));
byId('select-none').addEventListener('click', () => setAllScanned(false));
byId('batch-source-directory').addEventListener('input', clearScan);
byId('batch-recursive').addEventListener('change', clearScan);
byId('add-identity').addEventListener('click', addIdentity);
byId('identity-picker').addEventListener('change', selectIdentity);
byId('history-worker').addEventListener('change', renderHistory);
byId('history-window').addEventListener('change', renderHistory);
window.addEventListener('hashchange', selectTabFromHash);
for (const button of document.querySelectorAll('[data-tab]')) {
  button.addEventListener('click', () => { location.hash = button.dataset.tab; });
}

function headers() {
  const result = { 'Content-Type': 'application/json' };
  const key = byId('admin-key').value.trim();
  if (key) result['X-BackBurner-Admin-Key'] = key;
  return result;
}

async function api(path, options = {}) {
  const response = await fetch(`/api/admin${path}`, { ...options, headers: { ...headers(), ...(options.headers || {}) } });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try { message = (await response.json()).error || message; } catch { /* no JSON body */ }
    if (response.status === 401) message = 'Admin key required or incorrect.';
    const error = new Error(message);
    error.status = response.status;
    throw error;
  }
  return response.status === 204 ? null : response.json();
}

async function connect() {
  const key = byId('admin-key').value.trim();
  if (!key) {
    sessionStorage.removeItem('backburner-admin-key');
    setAuth(false, 'Key required');
    showMessage('Paste the admin key and click Connect.', 'notice');
    return;
  }
  sessionStorage.setItem('backburner-admin-key', key);
  await refresh();
}

function setAuth(connected, text) {
  const status = byId('auth-status');
  status.textContent = text;
  status.className = connected ? 'status authenticated' : 'status locked';
}

function setConnected(connected) {
  const indicator = byId('connection-indicator');
  indicator.classList.toggle('disconnected', !connected);
  indicator.lastChild.textContent = connected ? ' Live' : ' Disconnected';
}

function selectTabFromHash() {
  const requested = location.hash.slice(1);
  const selected = ['dashboard', 'new-job', 'workers', 'history'].includes(requested) ? requested : 'dashboard';
  for (const button of document.querySelectorAll('[data-tab]')) {
    const active = button.dataset.tab === selected;
    button.classList.toggle('selected', active);
    button.setAttribute('aria-selected', String(active));
  }
  for (const panel of document.querySelectorAll('[data-tab-panel]')) panel.hidden = panel.dataset.tabPanel !== selected;
}

function selectIdentity() {
  state.identityId = byId('identity-picker').value;
  if (state.identityId) localStorage.setItem('backburner-identity-id', state.identityId);
  else localStorage.removeItem('backburner-identity-id');
}

async function addIdentity() {
  const displayName = prompt('Name for this local identity');
  if (!displayName?.trim()) return;
  try {
    const identity = await api('/identities', { method: 'POST', body: JSON.stringify({ displayName }) });
    state.identityId = identity.id;
    localStorage.setItem('backburner-identity-id', identity.id);
    showMessage(`Now working as ${identity.displayName}.`);
    await refresh();
  } catch (error) { handleError(error); }
}

function renderIdentities(identities) {
  state.identities = identities || [];
  if (!state.identities.some(item => item.id === state.identityId)) {
    state.identityId = state.identities.length === 1 ? state.identities[0].id : '';
    if (state.identityId) localStorage.setItem('backburner-identity-id', state.identityId);
    else localStorage.removeItem('backburner-identity-id');
  }
  byId('identity-picker').replaceChildren(
    new Option(state.identities.length ? 'Choose an identity' : 'No identities yet', ''),
    ...state.identities.map(item => new Option(item.displayName, item.id))
  );
  byId('identity-picker').value = state.identityId;
}

function requireIdentity() {
  const identity = state.identities.find(item => item.id === state.identityId);
  if (!identity) {
    showMessage('Choose or create an identity before queueing work so the audit history records who requested it.', 'error');
    return null;
  }
  return identity;
}

function settingsFromForm() {
  const optionalInt = id => byId(id).value ? Number(byId(id).value) : null;
  return {
    container: byId('container').value, videoEncoder: byId('video-encoder').value,
    quality: Number(byId('quality').value), encoderPreset: byId('encoder-preset').value,
    maxWidth: optionalInt('max-width'), maxHeight: optionalInt('max-height'),
    audioEncoder: byId('audio-encoder').value, audioBitrateKbps: null,
    allAudio: byId('all-audio').checked, allSubtitles: byId('all-subtitles').checked,
    includeChapterMarkers: byId('chapters').checked,
    extraArguments: byId('extra-arguments').value.split('\n').map(item => item.trim()).filter(Boolean)
  };
}

function settingsToForm(settings) {
  byId('container').value = settings.container;
  byId('video-encoder').value = settings.videoEncoder;
  byId('quality').value = settings.quality;
  byId('encoder-preset').value = settings.encoderPreset;
  byId('max-width').value = settings.maxWidth ?? '';
  byId('max-height').value = settings.maxHeight ?? '';
  byId('audio-encoder').value = settings.audioEncoder;
  byId('all-audio').checked = settings.allAudio;
  byId('all-subtitles').checked = settings.allSubtitles;
  byId('chapters').checked = settings.includeChapterMarkers;
  byId('extra-arguments').value = (settings.extraArguments || []).join('\n');
}

function loadPreset() {
  const preset = state.presets.find(item => item.id === byId('preset-picker').value);
  if (preset) settingsToForm(preset.settings);
}

function resetSettings() {
  byId('preset-picker').value = '';
  settingsToForm({ container: 'mkv', videoEncoder: 'x265', quality: 22, encoderPreset: 'medium', maxWidth: null, maxHeight: null, audioEncoder: 'copy', allAudio: true, allSubtitles: true, includeChapterMarkers: true, extraArguments: [] });
}

function setMode(mode) {
  state.mode = mode;
  byId('single-fields').hidden = mode !== 'single';
  byId('batch-fields').hidden = mode !== 'batch';
  byId('single-mode').classList.toggle('selected', mode === 'single');
  byId('batch-mode').classList.toggle('selected', mode === 'batch');
  byId('queue-work').textContent = mode === 'single' ? 'Queue encoding' : 'Queue selected files';
  for (const input of byId('single-fields').querySelectorAll('input, select')) input.disabled = mode !== 'single';
  for (const input of byId('batch-fields').querySelectorAll('input, select')) input.disabled = mode !== 'batch';
  byId('display-name').required = mode === 'single';
  byId('source-path').required = mode === 'single';
  byId('destination-relative').required = mode === 'single';
  byId('batch-name').required = mode === 'batch';
  byId('batch-source-directory').required = mode === 'batch';
  byId('batch-destination-directory').required = mode === 'batch';
}

async function savePreset() {
  const selected = state.presets.find(item => item.id === byId('preset-picker').value);
  const name = prompt('Preset name', selected?.name || '');
  if (!name) return;
  const description = prompt('Short description', selected?.description || '') ?? '';
  try {
    await api('/presets', { method: 'POST', body: JSON.stringify({ name, description, settings: settingsFromForm() }) });
    showMessage(`Saved preset “${name}”.`);
    await refresh();
  } catch (error) { handleError(error); }
}

async function queueWork(event) {
  event.preventDefault();
  if (!requireIdentity()) return;
  if (state.mode === 'batch') await queueBatch();
  else await queueSingle();
}

async function queueSingle() {
  const preset = state.presets.find(item => item.id === byId('preset-picker').value);
  const relative = byId('destination-relative').value.replace(/^[/\\]+/, '');
  const request = {
    displayName: byId('display-name').value, sourcePath: byId('source-path').value,
    destinationPath: `${byId('destination-root').value}:/${relative}`,
    presetName: preset?.name || null, settings: settingsFromForm(),
    maxAttempts: Number(byId('max-attempts').value), identityId: state.identityId
  };
  try {
    await api('/jobs', { method: 'POST', body: JSON.stringify(request) });
    showMessage(`Queued “${request.displayName}”.`);
    byId('display-name').value = ''; byId('source-path').value = ''; byId('destination-relative').value = '';
    await refresh();
  } catch (error) { handleError(error); }
}

async function scanDirectory() {
  const directoryPath = byId('batch-source-directory').value.trim();
  if (!directoryPath) { showMessage('Enter a logical NAS directory before scanning.', 'error'); return; }
  const button = byId('scan-directory');
  button.disabled = true; button.textContent = 'Scanning…';
  try {
    const result = await api('/source/scan', { method: 'POST', body: JSON.stringify({ directoryPath, recursive: byId('batch-recursive').checked }) });
    state.scanFiles = result.files; state.scanDirectory = result.directoryPath;
    renderScannedFiles(result);
    showMessage(`Found ${result.files.length.toLocaleString()} video-file candidate${result.files.length === 1 ? '' : 's'}${result.truncated ? ' (result limit reached)' : ''}. Nothing has been selected or queued.`, 'notice');
  } catch (error) { clearScan(); handleError(error); }
  finally { button.disabled = false; button.textContent = 'Scan directory'; }
}

function renderScannedFiles(result) {
  byId('scan-result').hidden = result.files.length > 0;
  byId('scan-result').textContent = result.files.length ? '' : 'No supported video-file candidates were found.';
  byId('scan-selection').hidden = result.files.length === 0;
  byId('scan-table-wrap').hidden = result.files.length === 0;
  byId('scan-count').textContent = `${result.files.length.toLocaleString()} candidate${result.files.length === 1 ? '' : 's'} · 0 selected`;
  byId('scanned-files').replaceChildren(...result.files.map((file, index) => {
    const row = document.createElement('tr');
    const selection = document.createElement('td');
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox'; checkbox.className = 'scan-select'; checkbox.dataset.index = index;
    checkbox.addEventListener('change', updateSelectedCount); selection.append(checkbox);
    const name = textElement('td', file.relativePath); name.title = file.logicalPath;
    row.append(selection, name, textElement('td', formatBytes(file.sizeBytes)));
    return row;
  }));
}

function clearScan() {
  state.scanFiles = []; state.scanDirectory = null;
  byId('scan-selection').hidden = true; byId('scan-table-wrap').hidden = true; byId('scan-result').hidden = false;
  byId('scan-result').textContent = 'Scan a directory to find video-file candidates. Nothing is selected automatically.';
  byId('scanned-files').replaceChildren();
}

function setAllScanned(checked) {
  for (const box of document.querySelectorAll('.scan-select')) box.checked = checked;
  updateSelectedCount();
}

function updateSelectedCount() {
  const selected = document.querySelectorAll('.scan-select:checked').length;
  byId('scan-count').textContent = `${state.scanFiles.length.toLocaleString()} candidate${state.scanFiles.length === 1 ? '' : 's'} · ${selected.toLocaleString()} selected`;
}

async function queueBatch() {
  const selectedIndexes = [...document.querySelectorAll('.scan-select:checked')].map(box => Number(box.dataset.index));
  if (!state.scanDirectory || selectedIndexes.length === 0) { showMessage('Scan the directory and select at least one file.', 'error'); return; }
  const batchName = byId('batch-name').value.trim();
  const destinationDirectory = byId('batch-destination-directory').value.trim().replace(/^[/\\]+|[/\\]+$/g, '');
  const destinationRoot = byId('batch-destination-root').value;
  const container = byId('container').value;
  const preset = state.presets.find(item => item.id === byId('preset-picker').value);
  const items = selectedIndexes.map(index => {
    const file = state.scanFiles[index];
    return {
      displayName: `${batchName} · ${file.relativePath}`.slice(0, 200), sourcePath: file.logicalPath,
      destinationPath: `${destinationRoot}:/${destinationDirectory}/${replaceExtension(file.relativePath, container)}`
    };
  });
  const request = {
    displayName: batchName, sourceDirectory: state.scanDirectory, presetName: preset?.name || null,
    settings: settingsFromForm(), maxAttempts: Number(byId('max-attempts').value), identityId: state.identityId, items
  };
  try {
    await api('/batches', { method: 'POST', body: JSON.stringify(request) });
    showMessage(`Queued batch “${batchName}” as ${items.length} independent job${items.length === 1 ? '' : 's'}.`);
    clearScan(); byId('batch-name').value = ''; byId('batch-source-directory').value = ''; byId('batch-destination-directory').value = '';
    await refresh();
  } catch (error) { handleError(error); }
}

function replaceExtension(path, extension) {
  const slash = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
  const dot = path.lastIndexOf('.');
  return `${dot > slash ? path.slice(0, dot) : path}.${extension}`;
}

async function retryJob(id) {
  try {
    await api(`/jobs/${id}/retry?resetFailureCount=true`, { method: 'POST' });
    showMessage('Failure budget reset and job requeued.');
    await refresh();
  } catch (error) { handleError(error); }
}

async function setWorkerOwner(workerId, identityId) {
  try {
    await api(`/workers/${encodeURIComponent(workerId)}/owner`, { method: 'PUT', body: JSON.stringify({ identityId: identityId || null }) });
    showMessage(identityId ? 'Worker ownership updated.' : 'Worker ownership cleared.');
    await refresh();
  } catch (error) { handleError(error); }
}

function showMessage(message, tone = 'success') {
  byId('message').textContent = message;
  byId('message').className = tone;
}

function handleError(error) {
  if (error.status === 401) setAuth(false, 'Key rejected');
  setConnected(false);
  showMessage(error.message, 'error');
}

function render(snapshot) {
  state.snapshot = snapshot;
  state.presets = snapshot.presets || [];
  renderIdentities(snapshot.identities || []);
  const selectedPreset = byId('preset-picker').value;
  byId('preset-picker').replaceChildren(new Option('Unsaved settings', ''), ...state.presets.map(item => new Option(item.name, item.id)));
  byId('preset-picker').value = state.presets.some(item => item.id === selectedPreset) ? selectedPreset : '';

  renderDashboard(snapshot);
  renderWorkersAndQueue(snapshot);
  renderHistoryFilters(snapshot);
  renderHistory();
}

function renderDashboard(snapshot) {
  const jobs = snapshot.jobs || [];
  const workers = snapshot.workers || [];
  const activeJobs = jobs.filter(job => ACTIVE_STATUSES.has(job.status.toLowerCase()));
  const completed = jobs.filter(job => job.status.toLowerCase() === 'succeeded');
  const computeSeconds = jobs.reduce((sum, job) => sum + attemptSeconds(job.attempts || []), 0);
  const outputBytes = completed.reduce((sum, job) => sum + (job.outputBytes || 0), 0);
  const liveWorkers = workers.filter(worker => worker.availability.toLowerCase() !== 'offline');
  byId('summary-stats').replaceChildren(
    statCard(completed.length.toLocaleString(), 'Jobs completed', 'All retained work', 'green'),
    statCard(formatSpan(computeSeconds), 'Encoding time', `${activeJobs.length} active now`, 'blue'),
    statCard(formatBytes(outputBytes), 'Media produced', outputBytes ? 'Successful published outputs' : 'Starts counting on next completion', 'purple'),
    statCard(String(jobs.filter(job => job.status.toLowerCase() === 'queued').length), 'Jobs queued', `${jobs.filter(job => job.status.toLowerCase() === 'failed').length} need attention`, 'yellow'),
    statCard(`${liveWorkers.length}/${workers.length}`, 'Workers live', workers.length ? 'Heartbeat health' : 'Waiting for first worker', 'pink')
  );
  byId('active-count').textContent = activeJobs.length;
  byId('active-now').className = activeJobs.length ? 'cards' : 'cards empty';
  byId('active-now').replaceChildren(...(activeJobs.length ? activeJobs.map(activeJobCard) : [textElement('div', 'No encodes are running. Queued work will start when an eligible worker becomes available.') ]));
  byId('live-worker-count').textContent = `${liveWorkers.length}/${workers.length}`;
  byId('dashboard-workers').className = workers.length ? 'dashboard-workers' : 'dashboard-workers empty';
  byId('dashboard-workers').replaceChildren(...(workers.length ? workers.map(worker => dashboardWorkerRow(worker, jobs)) : [textElement('div', 'No workers have checked in.') ]));
  byId('dashboard-capacity').className = 'capacity-grid';
  byId('dashboard-capacity').replaceChildren(...capacityCards(workers, jobs));
}

function renderWorkersAndQueue(snapshot) {
  const jobs = snapshot.jobs || [];
  const workers = snapshot.workers || [];
  byId('worker-count').textContent = workers.length;
  byId('workers').className = workers.length ? 'worker-grid' : 'worker-grid empty';
  byId('workers').replaceChildren(...(workers.length ? workers.map(worker => workerCard(worker, jobs)) : [textElement('div', 'No workers have checked in.') ]));
  byId('capacity').className = 'capacity-grid';
  byId('capacity').replaceChildren(...capacityCards(workers, jobs));
  const batches = snapshot.batches || [];
  byId('batch-count').textContent = batches.length;
  byId('batches').className = batches.length ? 'cards' : 'cards empty';
  byId('batches').replaceChildren(...(batches.length ? batches.map(batch => batchCard(batch, jobs)) : [textElement('div', 'No directory batches yet.') ]));
  byId('jobs').replaceChildren(...(jobs.length ? jobs.map(jobRow) : [emptyRow(9, 'Nothing queued yet.') ]));
}

function statCard(value, label, note, tone) {
  const card = document.createElement('article');
  card.className = `stat-card stat-${tone}`;
  card.append(textElement('strong', value), textElement('span', label), textElement('small', note));
  return card;
}

function activeJobCard(job) {
  const card = document.createElement('article');
  card.className = 'active-job-card';
  const head = document.createElement('div'); head.className = 'worker-head';
  head.append(textElement('strong', job.displayName), statusPill(job.status));
  const bar = document.createElement('progress'); bar.max = 1; bar.value = job.progress;
  const worker = state.snapshot.workers.find(item => item.workerId === job.assignedWorkerId);
  card.append(head, bar, textElement('p', `${Math.round(job.progress * 100)}%${job.etaSeconds != null ? ` · ${formatEta(job.etaSeconds)}` : ''}`), textElement('small', worker ? workerDisplayName(worker) : (job.assignedWorkerId || 'Awaiting worker')));
  return card;
}

function dashboardWorkerRow(worker, jobs) {
  const presentation = workerPresentation(worker, jobs);
  const row = document.createElement('article');
  row.className = `dashboard-worker worker-${presentation.tone}`;
  const label = document.createElement('div');
  label.append(textElement('strong', workerDisplayName(worker)), textElement('small', presentation.label));
  const heartbeat = heartbeatElement(worker);
  row.append(label, heartbeat);
  return row;
}

function workerCard(worker, jobs) {
  const presentation = workerPresentation(worker, jobs);
  const activeJob = worker.availability.toLowerCase() !== 'offline' && worker.activeJobId ? jobs.find(job => job.id === worker.activeJobId) : null;
  const card = document.createElement('article');
  card.className = `worker worker-${presentation.tone}`;
  const head = document.createElement('div'); head.className = 'worker-head';
  head.append(textElement('strong', workerDisplayName(worker)), statusPill(worker.availability, presentation.label, presentation.tone));
  const identity = document.createElement('div'); identity.className = 'worker-identity';
  identity.append(textElement('span', workerRole(worker)), heartbeatElement(worker));
  const primary = textElement('p', activeJob ? `Encoding “${activeJob.displayName}”` : (worker.availabilityReason || 'Ready.'), 'worker-reason');
  card.append(head, identity, primary);
  if (activeJob) {
    const progress = document.createElement('div'); progress.className = 'worker-progress';
    const bar = document.createElement('progress'); bar.max = 1; bar.value = activeJob.progress;
    progress.append(bar, textElement('span', `${Math.round(activeJob.progress * 100)}%${activeJob.etaSeconds != null ? ` · ${formatEta(activeJob.etaSeconds)}` : ''}`));
    card.append(progress);
    if (worker.availabilityReason) card.append(textElement('p', worker.availabilityReason, 'worker-detail'));
  }
  if (worker.readyAt && !activeJob) {
    const countdown = textElement('p', readyCountdownText(worker.readyAt), 'worker-countdown'); countdown.dataset.readyAt = worker.readyAt; card.append(countdown);
  }
  const capabilities = document.createElement('div'); capabilities.className = 'capability-list';
  for (const capability of worker.capabilities || []) capabilities.append(textElement('span', capabilityLabel(capability), 'capability'));
  if (!worker.capabilities?.length) capabilities.append(textElement('span', 'No capabilities reported', 'capability capability-muted'));
  card.append(capabilities);
  const hardware = workerHardware(worker); if (hardware) card.append(textElement('p', hardware, 'worker-detail'));
  const owner = document.createElement('label'); owner.className = 'worker-owner'; owner.append(document.createTextNode('Machine owner'));
  const ownerSelect = document.createElement('select');
  ownerSelect.append(new Option('Unassigned', ''), ...state.identities.map(item => new Option(item.displayName, item.id)));
  ownerSelect.value = worker.ownerIdentityId || '';
  ownerSelect.addEventListener('change', () => setWorkerOwner(worker.workerId, ownerSelect.value));
  owner.append(ownerSelect); card.append(owner);
  return card;
}

function heartbeatElement(worker) {
  const ageSeconds = Math.max(0, Math.floor((Date.now() - new Date(worker.lastSeenAt).getTime()) / 1000));
  const freshness = worker.availability.toLowerCase() === 'offline' || ageSeconds > 60 ? 'offline' : ageSeconds > 20 ? 'stale' : 'live';
  const item = document.createElement('span'); item.className = `heartbeat heartbeat-${freshness}`;
  item.dataset.heartbeatAt = worker.lastSeenAt;
  const dot = document.createElement('i');
  item.append(dot, document.createTextNode(` Heartbeat ${freshness === 'live' ? 'live' : freshness} · ${formatAge(worker.lastSeenAt)}`));
  return item;
}

function capacityCards(workers, jobs) {
  const lanes = [
    { key: 'cpu', title: 'CPU encoding', description: 'Software H.264 / H.265' },
    { key: 'gpu', title: 'GPU encoding', description: 'NVENC, Quick Sync, or VCN' },
    { key: 'upscale', title: 'AI upscaling', description: 'Open-source model work' }
  ];
  return lanes.map(lane => {
    const capable = workers.filter(worker => workerSupportsLane(worker, lane.key));
    const ready = capable.filter(worker => worker.availability.toLowerCase() === 'available' && !worker.activeJobId);
    const working = capable.filter(worker => worker.activeJobId && worker.availability.toLowerCase() !== 'offline');
    const queued = jobs.filter(job => job.status.toLowerCase() === 'queued' && workClass(job).key === lane.key);
    const card = document.createElement('article'); card.className = 'capacity-card';
    const head = document.createElement('div'); head.className = 'capacity-head';
    head.append(textElement('strong', lane.title), textElement('span', `${queued.length} queued`, 'capacity-queue'));
    const numbers = document.createElement('div'); numbers.className = 'capacity-numbers';
    numbers.append(capacityNumber(ready.length, 'ready'), capacityNumber(working.length, 'working'), capacityNumber(capable.length, 'capable'));
    const roster = document.createElement('div'); roster.className = 'capacity-roster';
    if (capable.length) {
      for (const worker of capable) {
        const status = workerPresentation(worker, jobs);
        roster.append(textElement('span', `${workerDisplayName(worker)} · ${status.shortLabel}`, `capacity-worker capacity-worker-${status.tone}`));
      }
    } else roster.append(textElement('span', 'No capable workers registered', 'capacity-none'));
    card.append(head, textElement('p', lane.description), numbers, roster); return card;
  });
}

function capacityNumber(value, label) {
  const item = document.createElement('span'); item.append(textElement('strong', String(value)), document.createTextNode(` ${label}`)); return item;
}

function workerPresentation(worker, jobs) {
  const availability = worker.availability.toLowerCase();
  if (availability === 'offline') return { label: 'Offline', shortLabel: 'offline', tone: 'blocked' };
  const activeJob = worker.activeJobId ? jobs.find(job => job.id === worker.activeJobId) : null;
  if (activeJob) {
    if (activeJob.status.toLowerCase() === 'paused') return { label: 'Encoding paused', shortLabel: 'paused', tone: 'working' };
    if (availability === 'draining') return { label: 'Encoding · draining', shortLabel: 'draining', tone: 'working' };
    return { label: `Encoding · ${workClass(activeJob).label}`, shortLabel: 'working', tone: 'working' };
  }
  const category = (worker.blockingCategory || 'None').toLowerCase();
  if (category === 'humanactivity') return { label: 'Human activity', shortLabel: 'human active', tone: 'human' };
  if (category === 'idlecooldown') return { label: 'Idle cooldown', shortLabel: 'cooldown', tone: 'cooldown' };
  if (category === 'agentwork') return { label: 'Agent work active', shortLabel: 'agent active', tone: 'blocked' };
  if (category === 'agentreserved') return { label: 'Reserved by agent', shortLabel: 'agent reserved', tone: 'blocked' };
  if (category === 'systembusy') return { label: 'System busy', shortLabel: 'system busy', tone: 'cooldown' };
  const activityState = (worker.activityState || 'None').toLowerCase();
  if (activityState === 'idlecooldown') return { label: 'Idle cooldown', shortLabel: 'cooldown', tone: 'cooldown' };
  if (activityState === 'humanactive') return { label: 'Human activity', shortLabel: 'human active', tone: 'human' };
  const states = {
    available: { label: 'Available · no work', shortLabel: 'ready', tone: 'ready' },
    humanactive: { label: 'Human activity', shortLabel: 'human active', tone: 'human' },
    draining: { label: 'Draining', shortLabel: 'draining', tone: 'working' },
    gameworkerreserved: { label: 'Reserved by agent', shortLabel: 'agent reserved', tone: 'blocked' },
    inhibited: { label: 'Higher-priority work', shortLabel: 'inhibited', tone: 'blocked' },
    pausedbyoperator: { label: 'Operator paused', shortLabel: 'paused', tone: 'cooldown' },
    misconfigured: { label: 'Needs configuration', shortLabel: 'misconfigured', tone: 'blocked' },
    offline: { label: 'Offline', shortLabel: 'offline', tone: 'blocked' }
  };
  return states[availability] || { label: splitCase(worker.availability), shortLabel: splitCase(worker.availability).toLowerCase(), tone: 'blocked' };
}

function workerDisplayName(worker) {
  const hostname = worker.profile?.hostname || worker.displayName.replace(/\s+(personal desktop|game worker|render node)$/i, '');
  const categories = { personaldesktop: 'Personal Desktop', sharedgameworker: 'Game AI Runner', dedicatedrendernode: 'Render Node' };
  return `${hostname} (${categories[(worker.mode || '').toLowerCase()] || splitCase(worker.mode || 'Worker')})`;
}

function workerRole(worker) {
  const platform = /windows/i.test(worker.profile?.os || '') ? 'Windows' : /linux|ubuntu/i.test(worker.profile?.os || '') ? 'Ubuntu' : '';
  const roles = {
    personaldesktop: `${platform || 'Human-operated'} personal workstation`,
    sharedgameworker: `Cody game-development ${platform || 'Ubuntu'} runner`,
    dedicatedrendernode: `${platform ? `${platform} ` : ''}dedicated render node`
  };
  return roles[(worker.mode || '').toLowerCase()] || splitCase(worker.mode || 'Worker');
}

function workerHardware(worker) {
  const profile = worker.profile || {};
  return [profile.gpu, profile.ram, profile.logicalProcessors ? `${profile.logicalProcessors} logical CPUs` : null].filter(Boolean).join(' · ');
}

function capabilityLabel(capability) {
  const labels = { handbrake: 'HandBrake', 'encode:x264': 'CPU H.264', 'encode:x265': 'CPU H.265', 'encode:nvenc_h265': 'NVIDIA H.265', 'encode:qsv_h265': 'Intel H.265', 'encode:vcn_h265': 'AMD H.265', 'encode:vce_h265': 'AMD H.265' };
  if (labels[capability.toLowerCase()]) return labels[capability.toLowerCase()];
  if (capability.toLowerCase().startsWith('upscale:')) return `Upscale · ${capability.split(':').slice(1).join(':')}`;
  return capability;
}

function workerSupportsLane(worker, lane) {
  const capabilities = (worker.capabilities || []).map(item => item.toLowerCase());
  if (lane === 'upscale') return capabilities.some(item => item.startsWith('upscale:'));
  if (lane === 'gpu') return capabilities.some(item => /^encode:(nvenc|qsv|vcn|vce)/.test(item));
  return capabilities.some(item => item === 'encode:x264' || item === 'encode:x265');
}

function workClass(job) {
  const capabilities = (job.requiredCapabilities || []).map(item => item.toLowerCase());
  if (capabilities.some(item => item.startsWith('upscale:'))) return { key: 'upscale', label: 'AI upscale' };
  if (capabilities.some(item => /^encode:(nvenc|qsv|vcn|vce)/.test(item))) return { key: 'gpu', label: 'GPU encode' };
  return { key: 'cpu', label: 'CPU encode' };
}

function batchCard(batch, jobs) {
  const batchJobs = jobs.filter(job => job.batchId === batch.id);
  const succeeded = batchJobs.filter(job => job.status.toLowerCase() === 'succeeded').length;
  const failed = batchJobs.filter(job => job.status.toLowerCase() === 'failed').length;
  const active = batchJobs.filter(job => ACTIVE_STATUSES.has(job.status.toLowerCase())).length;
  const status = succeeded === batchJobs.length ? 'Succeeded' : failed ? 'Attention' : active ? 'Running' : 'Queued';
  const card = document.createElement('article'); card.className = 'worker batch-card';
  const head = document.createElement('div'); head.className = 'worker-head'; head.append(textElement('strong', batch.displayName), statusPill(status));
  card.append(head, textElement('p', `${succeeded}/${batchJobs.length} complete${failed ? ` · ${failed} failed` : ''}${active ? ` · ${active} active` : ''}`), textElement('p', `${batch.sourceDirectory} · submitted by ${batch.submittedBy}`));
  return card;
}

function jobRow(job) {
  const row = document.createElement('tr');
  const title = document.createElement('td'); title.append(textElement('strong', job.displayName), textElement('small', `${job.sourcePath} → ${job.destinationPath}`));
  const work = workClass(job); const workType = document.createElement('td'); workType.append(textElement('span', work.label, `work-class work-class-${work.key}`));
  const status = document.createElement('td'); status.append(statusPill(job.status));
  const progress = document.createElement('td'); const bar = document.createElement('progress'); bar.max = 1; bar.value = job.progress;
  progress.append(bar, textElement('small', `${Math.round(job.progress * 100)}%${job.etaSeconds != null ? ` · ${formatEta(job.etaSeconds)}` : ''}`));
  const attempts = textElement('td', `${job.failureCount}/${job.maxAttempts}`); attempts.append(textElement('small', `${job.interruptionCount} interruption${job.interruptionCount === 1 ? '' : 's'}`));
  const worker = state.snapshot.workers.find(item => item.workerId === job.assignedWorkerId);
  const action = document.createElement('td');
  if (job.status.toLowerCase() === 'failed') { const button = textElement('button', 'Retry', 'small'); button.addEventListener('click', () => retryJob(job.id)); action.append(button); }
  row.append(title, workType, status, progress, attempts, textElement('td', worker ? workerDisplayName(worker) : (job.assignedWorkerId || '—')), textElement('td', job.submittedBy || 'web'), textElement('td', job.lastError || (job.nextEligibleAt ? `Eligible ${new Date(job.nextEligibleAt).toLocaleTimeString()}` : '—')), action);
  return row;
}

function renderHistoryFilters(snapshot) {
  const selected = byId('history-worker').value;
  byId('history-worker').replaceChildren(new Option('All workers', ''), ...snapshot.workers.map(worker => new Option(workerDisplayName(worker), worker.workerId)));
  byId('history-worker').value = snapshot.workers.some(worker => worker.workerId === selected) ? selected : '';
}

function renderHistory() {
  if (!state.snapshot) return;
  const snapshot = state.snapshot;
  const workerId = byId('history-worker').value;
  const windowValue = byId('history-window').value;
  const now = Date.now();
  const allTimes = [...(snapshot.workerActivities || []).map(item => new Date(item.startedAt).getTime()), ...(snapshot.jobs || []).map(item => new Date(item.createdAt).getTime())].filter(Number.isFinite);
  const defaultStart = now - 24 * 60 * 60 * 1000;
  const start = windowValue === 'all' ? Math.min(...(allTimes.length ? allTimes : [defaultStart])) : now - Number(windowValue) * 60 * 60 * 1000;
  const activities = (snapshot.workerActivities || []).filter(item => (!workerId || item.workerId === workerId) && intervalEnd(item, now) >= start);
  const jobs = (snapshot.jobs || []).filter(job => {
    if (new Date(job.updatedAt).getTime() < start) return false;
    return !workerId || (job.attempts || []).some(attempt => attempt.workerId === workerId);
  });
  const computeSeconds = jobs.reduce((sum, job) => sum + attemptSeconds(job.attempts || [], start, now, workerId), 0);
  const productiveSeconds = activities.filter(item => PRODUCTIVE_KINDS.has(item.kind.toLowerCase())).reduce((sum, item) => sum + clippedSeconds(item, start, now), 0);
  const completed = jobs.filter(job => job.status.toLowerCase() === 'succeeded').length;
  const interrupted = jobs.reduce((sum, job) => sum + (job.attempts || []).filter(attempt => attempt.outcome.toLowerCase() === 'interrupted' && (!workerId || attempt.workerId === workerId)).length, 0);
  byId('history-stats').replaceChildren(
    statCard(String(completed), 'Completed', 'In selected window', 'green'),
    statCard(formatSpan(computeSeconds), 'Attempt time', 'All encoder attempts', 'blue'),
    statCard(formatSpan(productiveSeconds), 'Productive fleet time', 'Encoding, upscaling, or draining', 'purple'),
    statCard(String(interrupted), 'Interruptions', 'Do not consume retry budget', 'yellow')
  );
  renderTimelineLegend();
  renderTimelines(activities, snapshot.workers, snapshot.jobs, start, now, workerId);
  byId('job-history').replaceChildren(...(jobs.length ? jobs.map(historyJobRow) : [emptyRow(6, 'No matching jobs in this window.') ]));
  const events = (snapshot.events || []).filter(item => new Date(item.at).getTime() >= start && (!workerId || item.workerId === workerId));
  byId('events').replaceChildren(...(events.length ? events.map(eventRow) : [textElement('li', 'No matching events in this window.', 'empty') ]));
}

const timelineKinds = [
  ['encodingcpu', 'CPU encode'], ['encodinggpu', 'GPU encode'], ['upscaling', 'Upscaling'], ['draining', 'Finishing / draining'],
  ['humanactivity', 'Human activity'], ['idlecooldown', 'Idle cooldown'], ['agentwork', 'Agent work'], ['agentreserved', 'Agent reserved'],
  ['systembusy', 'System busy'], ['availablenowork', 'No work'], ['offline', 'Offline'], ['misconfigured', 'Needs attention']
];

function renderTimelineLegend() {
  byId('timeline-legend').replaceChildren(...timelineKinds.map(([key, label]) => {
    const item = document.createElement('span'); item.append(textElement('i', '', `timeline-swatch timeline-${key}`), document.createTextNode(label)); return item;
  }));
}

function renderTimelines(activities, workers, jobs, start, end, selectedWorkerId) {
  const visibleWorkers = selectedWorkerId ? workers.filter(worker => worker.workerId === selectedWorkerId) : workers;
  const rows = visibleWorkers.map(worker => {
    const workerActivities = activities.filter(item => item.workerId === worker.workerId).sort((a, b) => new Date(a.startedAt) - new Date(b.startedAt));
    const row = document.createElement('article'); row.className = 'timeline-row';
    const header = document.createElement('div'); header.className = 'timeline-header';
    const useful = workerActivities.filter(item => PRODUCTIVE_KINDS.has(item.kind.toLowerCase())).reduce((sum, item) => sum + clippedSeconds(item, start, end), 0);
    header.append(textElement('strong', workerDisplayName(worker)), textElement('small', `${formatSpan(useful)} productive`));
    const track = document.createElement('div'); track.className = 'timeline-track';
    if (!workerActivities.length) track.append(textElement('span', 'No recorded transitions in this window', 'timeline-empty'));
    for (const activity of workerActivities) {
      const segmentStart = Math.max(start, new Date(activity.startedAt).getTime());
      const segmentEnd = Math.min(end, intervalEnd(activity, end));
      const segment = document.createElement('span');
      const kind = activity.kind.toLowerCase();
      segment.className = `timeline-segment timeline-${kind}`;
      segment.style.left = `${((segmentStart - start) / (end - start)) * 100}%`;
      segment.style.width = `${Math.max(.25, ((segmentEnd - segmentStart) / (end - start)) * 100)}%`;
      const job = activity.jobId ? jobs.find(item => item.id === activity.jobId) : null;
      segment.title = `${activityLabel(kind)}${job ? ` · ${job.displayName}` : ''}\n${new Date(segmentStart).toLocaleString()} – ${new Date(segmentEnd).toLocaleString()}\n${activity.reason || ''}`;
      track.append(segment);
    }
    row.append(header, track); return row;
  });
  byId('worker-timelines').className = rows.length ? 'timelines' : 'timelines empty';
  byId('worker-timelines').replaceChildren(...(rows.length ? rows : [textElement('div', 'No workers match this view.') ]));
}

function historyJobRow(job) {
  const row = document.createElement('tr');
  const name = document.createElement('td'); name.append(textElement('strong', job.displayName), textElement('small', workClass(job).label));
  const status = document.createElement('td'); status.append(statusPill(job.status));
  const workers = [...new Set((job.attempts || []).map(item => item.workerId))].map(id => state.snapshot.workers.find(worker => worker.workerId === id)).filter(Boolean).map(workerDisplayName);
  row.append(name, status, textElement('td', workers.join(', ') || 'Not started'), textElement('td', formatSpan(attemptSeconds(job.attempts || []))), textElement('td', job.submittedBy || 'web'), textElement('td', new Date(job.updatedAt).toLocaleString()));
  return row;
}

function eventRow(item) {
  const row = document.createElement('li');
  const time = textElement('time', new Date(item.at).toLocaleString()); time.dateTime = item.at;
  row.append(time, textElement('span', item.message)); return row;
}

function attemptSeconds(attempts, start = -Infinity, end = Date.now(), workerId = '') {
  return attempts.filter(item => !workerId || item.workerId === workerId).reduce((sum, item) => {
    const itemStart = Math.max(start, new Date(item.startedAt).getTime());
    const itemEnd = Math.min(end, item.finishedAt ? new Date(item.finishedAt).getTime() : end);
    return sum + Math.max(0, itemEnd - itemStart) / 1000;
  }, 0);
}

function intervalEnd(item, fallback) { return item.endedAt ? new Date(item.endedAt).getTime() : fallback; }
function clippedSeconds(item, start, end) { return Math.max(0, Math.min(end, intervalEnd(item, end)) - Math.max(start, new Date(item.startedAt).getTime())) / 1000; }
function activityLabel(kind) { return timelineKinds.find(item => item[0] === kind)?.[1] || splitCase(kind); }
function statusPill(value, label = splitCase(value), tone = value.toLowerCase()) { return textElement('span', label, `status ${value.toLowerCase()} status-${tone}`); }
function splitCase(value) { return String(value).replace(/([a-z])([A-Z])/g, '$1 $2'); }

function formatAge(value) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 10) return 'just now';
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  return `${Math.floor(seconds / 3600)}h ago`;
}

function formatCountdown(seconds) {
  const whole = Math.max(0, Math.ceil(seconds)); const minutes = Math.floor(whole / 60); const remainder = whole % 60;
  return minutes ? `${minutes}m ${String(remainder).padStart(2, '0')}s` : `${remainder}s`;
}

function readyCountdownText(value) {
  const seconds = (new Date(value).getTime() - Date.now()) / 1000;
  return seconds > 0 ? `Earliest available in ${formatCountdown(seconds)}` : 'Idle window complete; waiting for the next worker check.';
}

function updateTemporalText() {
  for (const element of document.querySelectorAll('[data-ready-at]')) element.textContent = readyCountdownText(element.dataset.readyAt);
  for (const element of document.querySelectorAll('[data-heartbeat-at]')) {
    const ageSeconds = Math.max(0, Math.floor((Date.now() - new Date(element.dataset.heartbeatAt).getTime()) / 1000));
    const freshness = ageSeconds > 60 ? 'offline' : ageSeconds > 20 ? 'stale' : 'live';
    element.className = `heartbeat heartbeat-${freshness}`;
    element.lastChild.textContent = ` Heartbeat ${freshness} · ${formatAge(element.dataset.heartbeatAt)}`;
  }
}

function formatEta(seconds) { return `${formatSpan(seconds)} left`; }
function formatSpan(seconds) {
  if (!Number.isFinite(seconds) || seconds <= 0) return '0m';
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60); const remainder = minutes % 60;
  if (hours < 24) return `${hours}h ${remainder}m`;
  return `${Math.floor(hours / 24)}d ${hours % 24}h`;
}

function formatBytes(bytes) {
  if (!bytes) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB']; let value = bytes / 1024; let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(value >= 10 ? 1 : 2)} ${units[unit]}`;
}

function textElement(tag, text, className = '') { const element = document.createElement(tag); element.textContent = text; element.className = className; return element; }
function emptyRow(columns, message) { const row = document.createElement('tr'); const cell = textElement('td', message, 'empty'); cell.colSpan = columns; row.append(cell); return row; }

async function refresh() {
  if (state.requiresAuthentication && !byId('admin-key').value.trim()) {
    setAuth(false, 'Key required'); setConnected(false);
    showMessage('Paste the admin key above and click Connect.', 'notice'); return;
  }
  try {
    render(await api('/snapshot'));
    setAuth(true, 'Connected'); setConnected(true);
    if (/admin key|disconnected/i.test(byId('message').textContent)) showMessage('Connected to the coordinator.');
  } catch (error) { handleError(error); }
}

async function initialize() {
  selectTabFromHash(); setMode('single');
  try {
    const response = await fetch('/api/config');
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    const configuration = await response.json();
    state.requiresAuthentication = configuration.requiresAuthentication !== false;
    byId('auth-box').hidden = !state.requiresAuthentication;
    await refresh();
  } catch (error) { handleError(error); }
}

initialize();
setInterval(refresh, 5000);
setInterval(updateTemporalText, 1000);
