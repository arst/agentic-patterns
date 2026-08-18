const $ = (id) => document.getElementById(id);

const CATEGORY_ORDER = [
    'Fundamentals', 'Reasoning', 'Multi-agent', 'Knowledge & context',
    'Learning & goals', 'Reliability & operations'
];

let patterns = [];
let current = null;
let flavor = null;
let stream = null;
let lines = [];
let pendingRender = false;

// ---------- markdown ----------

marked.use({
    renderer: {
        code(token) {
            if (token.lang === 'mermaid') return `<pre class="mermaid">${escapeHtml(token.text)}</pre>`;
            return `<pre class="code"><code>${escapeHtml(token.text)}</code></pre>`;
        }
    }
});

mermaid.initialize({ startOnLoad: false, theme: 'dark', securityLevel: 'loose' });

function escapeHtml(text) {
    return text.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
}

// ---------- catalog ----------

async function loadCatalog() {
    patterns = await (await fetch('/api/patterns')).json();
    renderList();
    if (location.hash) select(location.hash.slice(1));
}

function renderList() {
    const filter = $('filter').value.trim().toLowerCase();
    const matches = patterns.filter((p) =>
        !filter || (p.title + ' ' + p.summary + ' ' + p.category).toLowerCase().includes(filter));

    const categories = [...new Set(matches.map((p) => p.category))].sort(
        (a, b) => index(a) - index(b) || a.localeCompare(b));

    $('list').innerHTML = categories.map((category) => `
        <div class="category">${escapeHtml(category)}</div>
        ${matches.filter((p) => p.category === category).map((p) => `
            <a href="#${p.id}" data-id="${p.id}">
                ${escapeHtml(p.title)}
                <span class="flavor-dots">${[...new Set(p.projects.map((x) => x.flavor.startsWith('SemanticKernel') ? 'SK' : 'AF'))].join(' ')}</span>
            </a>`).join('')}
    `).join('');

    highlightActive();
}

const index = (category) => {
    const i = CATEGORY_ORDER.indexOf(category);
    return i < 0 ? CATEGORY_ORDER.length : i;
};

function highlightActive() {
    document.querySelectorAll('#list a').forEach((a) =>
        a.classList.toggle('active', current !== null && a.dataset.id === current.id));
}

// ---------- pattern view ----------

async function select(id) {
    const response = await fetch(`/api/patterns/${encodeURIComponent(id)}`);
    if (!response.ok) return;

    current = await response.json();
    flavor = current.projects[0]?.flavor ?? null;

    $('empty').hidden = true;
    $('pattern').hidden = false;
    $('title').textContent = current.title;
    $('summary').textContent = current.summary;
    $('doc').innerHTML = marked.parse(current.body);
    mermaid.run({ nodes: $('doc').querySelectorAll('pre.mermaid') });

    renderFlavors();
    highlightActive();
    document.querySelector('main').scrollTop = 0;
}

function renderFlavors() {
    $('flavors').innerHTML = current.projects.map((p) => `
        <button data-flavor="${p.flavor}" class="${p.flavor === flavor ? 'active' : ''}">
            ${escapeHtml(flavorLabel(p.flavor))}
        </button>`).join('');

    const project = currentProject();
    $('note').textContent = project?.note ?? '';
    $('run').disabled = !project;
    renderSources();
}

const currentProject = () => current?.projects.find((p) => p.flavor === flavor);

const flavorLabel = (name) => name
    .replace('SemanticKernel', 'Semantic Kernel')
    .replace('AgentFramework', 'Agent Framework');

function renderSources() {
    const files = current.sources[flavor] ?? [];
    $('source-view').hidden = true;
    $('sources').innerHTML = files.map((f) =>
        `<button data-path="${f}">${escapeHtml(f)}</button>`).join('');
}

async function showSource(path, button) {
    const text = await (await fetch(`/api/source?path=${encodeURIComponent(path)}`)).text();
    $('source-view').textContent = text;
    $('source-view').hidden = false;
    document.querySelectorAll('#sources button').forEach((b) => b.classList.toggle('active', b === button));
}

// ---------- running ----------

function run() {
    if (stream) stopStream();

    lines = [];
    $('terminal').innerHTML = '';
    $('terminal-panel').hidden = false;
    $('terminal-title').textContent = `${current.title} · ${flavor}`;
    setStatus('running', 'running - each run calls your Azure OpenAI deployment');
    $('stdin-form').hidden = !currentProject().interactive;
    $('run').disabled = true;

    stream = new EventSource(`/api/run?id=${encodeURIComponent(current.id)}&flavor=${encodeURIComponent(flavor)}`);
    stream.onmessage = (event) => {
        const chunk = JSON.parse(event.data);
        append(chunk.s, chunk.t);
        scheduleRender();
    };
    stream.addEventListener('end', () => finish('done', 'finished'));
    stream.onerror = () => finish('done', 'stream closed');
}

function finish(state, message) {
    stopStream();
    setStatus(state, message);
    $('run').disabled = false;
}

function stopStream() {
    if (!stream) return;
    stream.close();
    stream = null;
}

function cancel() {
    fetch('/api/run/cancel', { method: 'POST' });
    finish('done', 'stopped');
}

function setStatus(state, message) {
    $('status').className = `status ${state}`;
    $('status').textContent = message;
}

// Chunks are raw (not line-delimited) so prompts written without a newline still show up.
function append(streamTag, text) {
    const parts = text.replace(/\r/g, '').split('\n');
    parts.forEach((part, i) => {
        const last = lines[lines.length - 1];
        if (i === 0 && last && last.open && last.stream === streamTag) last.text += part;
        else lines.push({ stream: streamTag, text: part, open: true });
        if (i < parts.length - 1) lines[lines.length - 1].open = false;
    });
}

function scheduleRender() {
    if (pendingRender) return;
    pendingRender = true;
    requestAnimationFrame(() => {
        pendingRender = false;
        const terminal = $('terminal');
        const atBottom = terminal.scrollHeight - terminal.scrollTop - terminal.clientHeight < 60;
        terminal.innerHTML = lines.map(format).join('\n');
        if (atBottom) terminal.scrollTop = terminal.scrollHeight;
    });
}

// Highlights the lines that show the pattern's mechanism: tool calls, speakers, phase banners.
function format(line) {
    const text = escapeHtml(line.text);
    if (line.stream !== 'out') return `<span class="l-${line.stream}">${text}</span>`;

    if (/^\s*[=\-*~_#]{3,}\s*$/.test(line.text)) return `<span class="t-banner">${text}</span>`;

    let html = text.replace(/^(\s*)(\[[^\]\n]{1,48}\])/, '$1<span class="t-tag">$2</span>');
    if (html === text)
        html = text.replace(/^(\s*)([A-Z][A-Za-z0-9 _.-]{0,28}):(?=\s|$)/, '$1<span class="t-speaker">$2:</span>');

    return html.replace(/\(y\/n\)/g, '<span class="t-prompt">(y/n)</span>');
}

// ---------- wiring ----------

$('filter').addEventListener('input', renderList);
$('list').addEventListener('click', (e) => {
    const link = e.target.closest('a');
    if (link) select(link.dataset.id);
});
window.addEventListener('hashchange', () => location.hash && select(location.hash.slice(1)));

$('flavors').addEventListener('click', (e) => {
    const button = e.target.closest('button');
    if (!button) return;
    flavor = button.dataset.flavor;
    renderFlavors();
});

$('sources').addEventListener('click', (e) => {
    const button = e.target.closest('button');
    if (button) showSource(button.dataset.path, button);
});

$('run').addEventListener('click', run);
$('cancel').addEventListener('click', cancel);
$('close-terminal').addEventListener('click', () => {
    cancel();
    $('terminal-panel').hidden = true;
});

$('stdin-form').addEventListener('submit', (e) => {
    e.preventDefault();
    fetch('/api/run/input', { method: 'POST', body: $('stdin').value });
    $('stdin').value = '';
});

loadCatalog();
