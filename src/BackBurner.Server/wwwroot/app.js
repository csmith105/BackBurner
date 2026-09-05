const byId = id => document.getElementById(id);
const state = { presets: [], scanFiles: [], scanDirectory: null, mode: 'single' };

byId('admin-key').value = sessionStorage.getItem('backburner-admin-key') || '';
byId('connect').addEventListener('click', connect);
byId('admin-key').addEventListener('keydown', event => { if (event.key === 'Enter') connect(); });
byId('refresh').addEventListener('click', refresh);
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

function headers() {
  return { 'Content-Type': 'application/json', 'X-BackBurner-Admin-Key': byId('admin-key').value.trim() };
}

async function api(path, options = {}) {
  const response = await fetch(`/api/admin${path}`, { ...options, headers: { ...headers(), ...(options.headers || {}) } });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try { message = (await response.json()).error || message; } catch { /* response had no JSON */ }
    if (response.status === 401) message = 'Admin key required or incorrect. Paste the deployment key above and click Connect.';
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
    showMessage('Paste the admin key and click Connect. “Unauthorized” only means the protected API has no valid key yet.', 'notice');
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

function settingsFromForm() {
  const optionalInt = id => byId(id).value ? Number(byId(id).value) : null;
  return {
    container: byId('container').value,
    videoEncoder: byId('video-encoder').value,
    quality: Number(byId('quality').value),
    encoderPreset: byId('encoder-preset').value,
    maxWidth: optionalInt('max-width'),
    maxHeight: optionalInt('max-height'),
    audioEncoder: byId('audio-encoder').value,
    audioBitrateKbps: null,
    allAudio: byId('all-audio').checked,
    allSubtitles: byId('all-subtitles').checked,
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
  if (state.mode === 'batch') await queueBatch();
  else await queueSingle();
}

async function queueSingle() {
  const preset = state.presets.find(item => item.id === byId('preset-picker').value);
  const relative = byId('destination-relative').value.replace(/^[/\\]+/, '');
  const request = {
    displayName: byId('display-name').value,
    sourcePath: byId('source-path').value,
    destinationPath: `${byId('destination-root').value}:/${relative}`,
    presetName: preset?.name || null,
    settings: settingsFromForm(),
    maxAttempts: Number(byId('max-attempts').value),
    submittedBy: 'web'
  };
  try {
    await api('/jobs', { method: 'POST', body: JSON.stringify(request) });
    showMessage(`Queued “${request.displayName}”.`);
    byId('display-name').value = '';
    byId('source-path').value = '';
    byId('destination-relative').value = '';
    await refresh();
  } catch (error) { handleError(error); }
}

async function scanDirectory() {
  const directoryPath = byId('batch-source-directory').value.trim();
  if (!directoryPath) {
    showMessage('Enter a logical NAS directory before scanning.', 'error');
    return;
  }
  const button = byId('scan-directory');
  button.disabled = true;
  button.textContent = 'Scanning…';
  try {
    const result = await api('/source/scan', {
      method: 'POST',
      body: JSON.stringify({ directoryPath, recursive: byId('batch-recursive').checked })
    });
    state.scanFiles = result.files;
    state.scanDirectory = result.directoryPath;
    renderScannedFiles(result);
    showMessage(`Found ${result.files.length.toLocaleString()} video-file candidate${result.files.length === 1 ? '' : 's'}${result.truncated ? ' (result limit reached)' : ''}. Nothing has been selected or queued.`, 'notice');
  } catch (error) {
    clearScan();
    handleError(error);
  } finally {
    button.disabled = false;
    button.textContent = 'Scan directory';
  }
}

function renderScannedFiles(result) {
  byId('scan-result').hidden = result.files.length > 0;
  byId('scan-result').textContent = result.files.length ? '' : 'No supported video-file candidates were found.';
  byId('scan-selection').hidden = result.files.length === 0;
  byId('scan-table-wrap').hidden = result.files.length === 0;
  byId('scan-count').textContent = `${result.files.length.toLocaleString()} candidate${result.files.length === 1 ? '' : 's'} · 0 selected`;
  const rows = result.files.map((file, index) => {
    const row = document.createElement('tr');
    const selection = document.createElement('td');
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'scan-select';
    checkbox.dataset.index = index;
    checkbox.addEventListener('change', updateSelectedCount);
    selection.append(checkbox);
    const name = textElement('td', file.relativePath);
    name.title = file.logicalPath;
    row.append(selection, name, textElement('td', formatBytes(file.sizeBytes)));
    return row;
  });
  byId('scanned-files').replaceChildren(...rows);
}

function clearScan() {
  state.scanFiles = [];
  state.scanDirectory = null;
  byId('scan-selection').hidden = true;
  byId('scan-table-wrap').hidden = true;
  byId('scan-result').hidden = false;
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
  if (!state.scanDirectory || selectedIndexes.length === 0) {
    showMessage('Scan the directory and select at least one file. Scanned files begin unchecked.', 'error');
    return;
  }
  const batchName = byId('batch-name').value.trim();
  const destinationDirectory = byId('batch-destination-directory').value.trim().replace(/^[/\\]+|[/\\]+$/g, '');
  const destinationRoot = byId('batch-destination-root').value;
  const container = byId('container').value;
  const preset = state.presets.find(item => item.id === byId('preset-picker').value);
  const items = selectedIndexes.map(index => {
    const file = state.scanFiles[index];
    const outputRelative = replaceExtension(file.relativePath, container);
    return {
      displayName: `${batchName} · ${file.relativePath}`.slice(0, 200),
      sourcePath: file.logicalPath,
      destinationPath: `${destinationRoot}:/${destinationDirectory}/${outputRelative}`
    };
  });
  const request = {
    displayName: batchName,
    sourceDirectory: state.scanDirectory,
    presetName: preset?.name || null,
    settings: settingsFromForm(),
    maxAttempts: Number(byId('max-attempts').value),
    submittedBy: 'web',
    items
  };
  try {
    await api('/batches', { method: 'POST', body: JSON.stringify(request) });
    showMessage(`Queued batch “${batchName}” as ${items.length} independent job${items.length === 1 ? '' : 's'}.`);
    clearScan();
    byId('batch-name').value = '';
    byId('batch-source-directory').value = '';
    byId('batch-destination-directory').value = '';
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

function showMessage(message, tone = 'success') {
  byId('message').textContent = message;
  byId('message').className = tone;
}

function handleError(error) {
  if (error.status === 401) setAuth(false, 'Key rejected');
  showMessage(error.message, 'error');
}

function render(snapshot) {
  state.presets = snapshot.presets;
  const selected = byId('preset-picker').value;
  byId('preset-picker').replaceChildren(new Option('Unsaved settings', ''), ...snapshot.presets.map(item => new Option(item.name, item.id)));
  byId('preset-picker').value = snapshot.presets.some(item => item.id === selected) ? selected : '';

  byId('worker-count').textContent = snapshot.workers.length;
  byId('capacity').className = 'capacity-grid';
  byId('capacity').replaceChildren(...capacityCards(snapshot.workers, snapshot.jobs));
  byId('workers').className = snapshot.workers.length ? 'worker-grid' : 'worker-grid empty';
  byId('workers').replaceChildren(...(snapshot.workers.length ? snapshot.workers.map(worker => workerCard(worker, snapshot.jobs)) : [textElement('div', 'No workers have checked in.')]));

  const batches = snapshot.batches || [];
  byId('batch-count').textContent = batches.length;
  byId('batches').className = batches.length ? 'cards' : 'cards empty';
  byId('batches').replaceChildren(...(batches.length ? batches.map(batch => batchCard(batch, snapshot.jobs)) : [textElement('div', 'No directory batches yet.')]));

  byId('jobs').replaceChildren(...(snapshot.jobs.length ? snapshot.jobs.map(jobRow) : [emptyRow()]));
  byId('events').replaceChildren(...(snapshot.events.length ? snapshot.events.map(eventRow) : [textElement('li', 'No events yet.', 'empty')]));
}

function workerCard(worker, jobs) {
  const presentation = workerPresentation(worker, jobs);
  const activeJob = worker.availability.toLowerCase() !== 'offline' && worker.activeJobId
    ? jobs.find(job => job.id === worker.activeJobId)
    : null;
  const card = document.createElement('article');
  card.className = `worker worker-${presentation.tone}`;
  const head = document.createElement('div');
  head.className = 'worker-head';
  head.append(textElement('strong', worker.displayName), statusPill(worker.availability, presentation.label, presentation.tone));

  const identity = document.createElement('div');
  identity.className = 'worker-identity';
  identity.append(textElement('span', workerRole(worker)), textElement('span', `Last seen ${formatAge(worker.lastSeenAt)}`));

  const primary = textElement('p', activeJob ? `Encoding “${activeJob.displayName}”` : (worker.availabilityReason || 'Ready.'), 'worker-reason');
  card.append(head, identity, primary);
  if (activeJob) {
    const progress = document.createElement('div');
    progress.className = 'worker-progress';
    const bar = document.createElement('progress');
    bar.max = 1;
    bar.value = activeJob.progress;
    progress.append(bar, textElement('span', `${Math.round(activeJob.progress * 100)}%${activeJob.etaSeconds != null ? ` · ${formatDuration(activeJob.etaSeconds)}` : ''}`));
    card.append(progress);
    if (worker.availabilityReason) card.append(textElement('p', worker.availabilityReason, 'worker-detail'));
  }
  if (worker.readyAt && !activeJob) {
    const countdown = textElement('p', readyCountdownText(worker.readyAt), 'worker-countdown');
    countdown.dataset.readyAt = worker.readyAt;
    card.append(countdown);
  }

  const capabilities = document.createElement('div');
  capabilities.className = 'capability-list';
  for (const capability of worker.capabilities || []) capabilities.append(textElement('span', capabilityLabel(capability), 'capability'));
  if (!worker.capabilities?.length) capabilities.append(textElement('span', 'No capabilities reported', 'capability capability-muted'));
  card.append(capabilities);

  const hardware = workerHardware(worker);
  if (hardware) card.append(textElement('p', hardware, 'worker-detail'));
  return card;
}

function capacityCards(workers, jobs) {
  const lanes = [
    { key: 'cpu', title: 'CPU encoding', description: 'Software H.264 / H.265' },
    { key: 'gpu', title: 'GPU encoding', description: 'NVENC, Quick Sync, or VCN' },
    { key: 'upscale', title: 'AI upscaling', description: 'Future open-source model work' }
  ];
  return lanes.map(lane => {
    const capable = workers.filter(worker => workerSupportsLane(worker, lane.key));
    const ready = capable.filter(worker => worker.availability.toLowerCase() === 'available' && !worker.activeJobId);
    const working = capable.filter(worker => worker.activeJobId && worker.availability.toLowerCase() !== 'offline');
    const queued = jobs.filter(job => job.status.toLowerCase() === 'queued' && workClass(job).key === lane.key);
    const card = document.createElement('article');
    card.className = 'capacity-card';
    const head = document.createElement('div');
    head.className = 'capacity-head';
    head.append(textElement('strong', lane.title), textElement('span', `${queued.length} queued`, 'capacity-queue'));
    const numbers = document.createElement('div');
    numbers.className = 'capacity-numbers';
    numbers.append(capacityNumber(ready.length, 'ready'), capacityNumber(working.length, 'working'), capacityNumber(capable.length, 'capable'));
    const roster = document.createElement('div');
    roster.className = 'capacity-roster';
    if (capable.length) {
      for (const worker of capable) {
        const status = workerPresentation(worker, jobs);
        roster.append(textElement('span', `${worker.displayName} · ${status.shortLabel}`, `capacity-worker capacity-worker-${status.tone}`));
      }
    } else {
      roster.append(textElement('span', 'No capable workers registered', 'capacity-none'));
    }
    card.append(head, textElement('p', lane.description), numbers, roster);
    return card;
  });
}

function capacityNumber(value, label) {
  const item = document.createElement('span');
  item.append(textElement('strong', String(value)), document.createTextNode(` ${label}`));
  return item;
}

function workerPresentation(worker, jobs) {
  const availability = worker.availability.toLowerCase();
  if (availability === 'offline') return { label: 'Offline', shortLabel: 'offline', tone: 'blocked' };
  const activeJob = worker.activeJobId ? jobs.find(job => job.id === worker.activeJobId) : null;
  if (activeJob) {
    if (activeJob.status.toLowerCase() === 'paused') return { label: 'Encoding paused', shortLabel: 'paused', tone: 'working' };
    if (availability === 'draining') return { label: 'Encoding · draining', shortLabel: 'draining', tone: 'working' };
    return { label: 'Encoding', shortLabel: 'working', tone: 'working' };
  }
  const activityState = (worker.activityState || 'None').toLowerCase();
  if (activityState === 'idlecooldown') return { label: 'Idle cooldown', shortLabel: 'cooldown', tone: 'cooldown' };
  if (activityState === 'humanactive') return { label: 'Human activity', shortLabel: 'human active', tone: 'blocked' };
  const states = {
    available: { label: 'Available', shortLabel: 'ready', tone: 'ready' },
    humanactive: { label: 'Human activity', shortLabel: 'human active', tone: 'blocked' },
    draining: { label: 'Draining', shortLabel: 'draining', tone: 'working' },
    gameworkerreserved: { label: 'Reserved by agent', shortLabel: 'agent reserved', tone: 'blocked' },
    inhibited: { label: 'Higher-priority work', shortLabel: 'inhibited', tone: 'blocked' },
    pausedbyoperator: { label: 'Operator paused', shortLabel: 'paused', tone: 'blocked' },
    misconfigured: { label: 'Needs configuration', shortLabel: 'misconfigured', tone: 'blocked' },
    offline: { label: 'Offline', shortLabel: 'offline', tone: 'blocked' }
  };
  return states[availability] || { label: splitCase(worker.availability), shortLabel: splitCase(worker.availability).toLowerCase(), tone: 'blocked' };
}

function workerRole(worker) {
  const platform = /windows/i.test(worker.profile?.os || '') ? 'Windows' : /linux|ubuntu/i.test(worker.profile?.os || '') ? 'Ubuntu' : '';
  const roles = {
    personaldesktop: `${platform || 'Human-operated'} personal workstation`,
    sharedgameworker: `Cody game-development ${platform || 'Ubuntu'} runner`,
    dedicatedrendernode: `${platform ? `${platform} ` : ''}dedicated render node`
  };
  return roles[(worker.mode || 'PersonalDesktop').toLowerCase()] || splitCase(worker.mode || 'Worker');
}

function workerHardware(worker) {
  const profile = worker.profile || {};
  return [profile.hostname, profile.gpu, profile.ram, profile.logicalProcessors ? `${profile.logicalProcessors} logical CPUs` : null].filter(Boolean).join(' · ');
}

function capabilityLabel(capability) {
  const labels = {
    handbrake: 'HandBrake',
    'encode:x264': 'CPU H.264',
    'encode:x265': 'CPU H.265',
    'encode:nvenc_h265': 'NVIDIA H.265',
    'encode:qsv_h265': 'Intel H.265',
    'encode:vcn_h265': 'AMD H.265',
    'encode:vce_h265': 'AMD H.265'
  };
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
  const active = batchJobs.filter(job => ['leased', 'running', 'paused'].includes(job.status.toLowerCase())).length;
  const status = succeeded === batchJobs.length ? 'Succeeded' : failed ? 'Attention' : active ? 'Running' : 'Queued';
  const card = document.createElement('article');
  card.className = 'worker batch-card';
  const head = document.createElement('div');
  head.className = 'worker-head';
  head.append(textElement('strong', batch.displayName), statusPill(status));
  card.append(head, textElement('p', `${succeeded}/${batchJobs.length} complete${failed ? ` · ${failed} failed` : ''}${active ? ` · ${active} active` : ''}`), textElement('p', batch.sourceDirectory));
  return card;
}

function jobRow(job) {
  const row = document.createElement('tr');
  const title = document.createElement('td');
  title.append(textElement('strong', job.displayName), textElement('small', `${job.sourcePath} → ${job.destinationPath}`));
  const work = workClass(job);
  const workType = document.createElement('td'); workType.append(textElement('span', work.label, `work-class work-class-${work.key}`));
  const status = document.createElement('td'); status.append(statusPill(job.status));
  const progress = document.createElement('td');
  const bar = document.createElement('progress'); bar.max = 1; bar.value = job.progress;
  progress.append(bar, textElement('small', `${Math.round(job.progress * 100)}%${job.etaSeconds != null ? ` · ${formatDuration(job.etaSeconds)}` : ''}`));
  const attempts = textElement('td', `${job.failureCount}/${job.maxAttempts}`);
  attempts.append(textElement('small', `${job.interruptionCount} interruption${job.interruptionCount === 1 ? '' : 's'}`));
  const worker = textElement('td', job.assignedWorkerId || '—');
  const note = textElement('td', job.lastError || (job.nextEligibleAt ? `Eligible ${new Date(job.nextEligibleAt).toLocaleTimeString()}` : '—'));
  const action = document.createElement('td');
  if (job.status.toLowerCase() === 'failed') {
    const button = textElement('button', 'Retry', 'small');
    button.addEventListener('click', () => retryJob(job.id));
    action.append(button);
  }
  row.append(title, workType, status, progress, attempts, worker, note, action);
  return row;
}

function eventRow(item) {
  const row = document.createElement('li');
  const time = textElement('time', new Date(item.at).toLocaleString());
  time.dateTime = item.at;
  row.append(time, textElement('span', item.message));
  return row;
}

function statusPill(value, label = splitCase(value), tone = value.toLowerCase()) { return textElement('span', label, `status ${value.toLowerCase()} status-${tone}`); }
function splitCase(value) { return value.replace(/([a-z])([A-Z])/g, '$1 $2'); }
function formatAge(value) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 10) return 'just now';
  if (seconds < 60) return `${seconds}s ago`;
  return `${Math.floor(seconds / 60)}m ago`;
}
function formatCountdown(seconds) {
  const whole = Math.max(0, Math.ceil(seconds));
  const minutes = Math.floor(whole / 60);
  const remainder = whole % 60;
  return minutes ? `${minutes}m ${String(remainder).padStart(2, '0')}s` : `${remainder}s`;
}
function readyCountdownText(value) {
  const seconds = (new Date(value).getTime() - Date.now()) / 1000;
  return seconds > 0 ? `Earliest available in ${formatCountdown(seconds)}` : 'Idle window complete; waiting for the next worker check.';
}
function updateCountdowns() {
  for (const element of document.querySelectorAll('[data-ready-at]')) {
    element.textContent = readyCountdownText(element.dataset.readyAt);
  }
}
function formatDuration(seconds) {
  if (seconds < 60) return `${seconds}s left`;
  const minutes = Math.ceil(seconds / 60);
  return minutes < 60 ? `${minutes}m left` : `${Math.floor(minutes / 60)}h ${minutes % 60}m left`;
}
function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(value >= 10 ? 1 : 2)} ${units[unit]}`;
}
function textElement(tag, text, className = '') { const element = document.createElement(tag); element.textContent = text; element.className = className; return element; }
function emptyRow() { const row = document.createElement('tr'); const cell = textElement('td', 'Nothing queued yet.', 'empty'); cell.colSpan = 8; row.append(cell); return row; }

async function refresh() {
  if (!byId('admin-key').value.trim()) {
    setAuth(false, 'Key required');
    showMessage('Paste the admin key above and click Connect. The coordinator is running; its administrative API is protected.', 'notice');
    return;
  }
  try {
    render(await api('/snapshot'));
    setAuth(true, 'Connected');
    if (/admin key|paste the admin key/i.test(byId('message').textContent)) showMessage('Connected to the coordinator.');
  } catch (error) { handleError(error); }
}

setMode('single');
refresh();
setInterval(refresh, 5000);
setInterval(updateCountdowns, 1000);
