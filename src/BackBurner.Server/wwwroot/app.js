const byId = id => document.getElementById(id);
const state = { presets: [] };

byId('admin-key').value = sessionStorage.getItem('backburner-admin-key') || '';
byId('admin-key').addEventListener('change', () => {
  sessionStorage.setItem('backburner-admin-key', byId('admin-key').value);
  refresh();
});
byId('refresh').addEventListener('click', refresh);
byId('job-form').addEventListener('submit', queueJob);
byId('save-preset').addEventListener('click', savePreset);
byId('reset-settings').addEventListener('click', resetSettings);
byId('preset-picker').addEventListener('change', loadPreset);

function headers() {
  return { 'Content-Type': 'application/json', 'X-BackBurner-Admin-Key': byId('admin-key').value };
}

async function api(path, options = {}) {
  const response = await fetch(`/api/admin${path}`, { ...options, headers: { ...headers(), ...(options.headers || {}) } });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try { message = (await response.json()).error || message; } catch { /* response had no JSON */ }
    throw new Error(message);
  }
  return response.status === 204 ? null : response.json();
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

async function savePreset() {
  const selected = state.presets.find(item => item.id === byId('preset-picker').value);
  const name = prompt('Preset name', selected?.name || '');
  if (!name) return;
  const description = prompt('Short description', selected?.description || '') ?? '';
  try {
    await api('/presets', { method: 'POST', body: JSON.stringify({ name, description, settings: settingsFromForm() }) });
    showMessage(`Saved preset “${name}”.`);
    await refresh();
  } catch (error) { showMessage(error.message, true); }
}

async function queueJob(event) {
  event.preventDefault();
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
  } catch (error) { showMessage(error.message, true); }
}

async function retryJob(id) {
  try {
    await api(`/jobs/${id}/retry?resetFailureCount=true`, { method: 'POST' });
    showMessage('Failure budget reset and job requeued.');
    await refresh();
  } catch (error) { showMessage(error.message, true); }
}

function showMessage(message, error = false) {
  byId('message').textContent = message;
  byId('message').className = error ? 'error' : '';
}

function render(snapshot) {
  state.presets = snapshot.presets;
  const selected = byId('preset-picker').value;
  byId('preset-picker').replaceChildren(new Option('Unsaved settings', ''), ...snapshot.presets.map(item => new Option(item.name, item.id)));
  byId('preset-picker').value = snapshot.presets.some(item => item.id === selected) ? selected : '';

  byId('worker-count').textContent = snapshot.workers.length;
  byId('workers').className = snapshot.workers.length ? 'cards' : 'cards empty';
  byId('workers').replaceChildren(...(snapshot.workers.length ? snapshot.workers.map(workerCard) : [textElement('div', 'No workers have checked in.')]));

  const jobs = byId('jobs');
  jobs.replaceChildren(...(snapshot.jobs.length ? snapshot.jobs.map(jobRow) : [emptyRow()]));

  const events = byId('events');
  events.replaceChildren(...(snapshot.events.length ? snapshot.events.map(eventRow) : [textElement('li', 'No events yet.', 'empty')]));
}

function workerCard(worker) {
  const card = document.createElement('article');
  card.className = 'worker';
  const head = document.createElement('div');
  head.className = 'worker-head';
  head.append(textElement('strong', worker.displayName), statusPill(worker.availability));
  card.append(head, textElement('p', worker.availabilityReason || 'Ready.'), textElement('p', worker.capabilities.join(' · ') || 'No capabilities reported'));
  return card;
}

function jobRow(job) {
  const row = document.createElement('tr');
  const title = document.createElement('td');
  title.append(textElement('strong', job.displayName), textElement('small', `${job.sourcePath} → ${job.destinationPath}`));
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
  row.append(title, status, progress, attempts, worker, note, action);
  return row;
}

function eventRow(item) {
  const row = document.createElement('li');
  const time = textElement('time', new Date(item.at).toLocaleString());
  time.dateTime = item.at;
  row.append(time, textElement('span', item.message));
  return row;
}

function statusPill(value) {
  return textElement('span', splitCase(value), `status ${value.toLowerCase()}`);
}

function splitCase(value) { return value.replace(/([a-z])([A-Z])/g, '$1 $2'); }
function formatDuration(seconds) {
  if (seconds < 60) return `${seconds}s left`;
  const minutes = Math.ceil(seconds / 60);
  return minutes < 60 ? `${minutes}m left` : `${Math.floor(minutes / 60)}h ${minutes % 60}m left`;
}
function textElement(tag, text, className = '') { const element = document.createElement(tag); element.textContent = text; element.className = className; return element; }
function emptyRow() { const row = document.createElement('tr'); const cell = textElement('td', 'Nothing queued yet.', 'empty'); cell.colSpan = 7; row.append(cell); return row; }

async function refresh() {
  try { render(await api('/snapshot')); }
  catch (error) { showMessage(error.message, true); }
}

refresh();
setInterval(refresh, 5000);
