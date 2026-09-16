// WindowCast Browser Client — Phase 6 (Full UI)
'use strict';

const STUN = { iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] };
const isMobile = /Mobi|Android/i.test(navigator.userAgent) || ('ontouchstart' in window && window.innerWidth < 900);

// ─── State ───
const state = {
    token: null,
    sessions: new Map(),   // id -> SessionState
    activeId: null,
    modifiers: { ctrl: false, alt: false, meta: false, shift: false },
    pickerMode: 'app',
    focused: false,
    isFullscreen: false,
    escapeForwardPending: false,
    authFailed: false,
    transport: 'webrtc',   // server reports 'ws-jpeg' / 'ws-h264' when it has no WebRTC (Windows)
};

// ─── Auth ───
function getToken() {
    const h = location.hash;
    if (h.startsWith('#token=')) { const t = h.slice(7); history.replaceState(null, '', location.pathname); return t; }
    return localStorage.getItem('wc_token');
}
function storeToken(t, remember) {
    (remember ? localStorage : sessionStorage).setItem('wc_token', t);
    state.token = t;
}
function clearToken() {
    localStorage.removeItem('wc_token');
    sessionStorage.removeItem('wc_token');
    state.token = null;
}

async function api(path, opts = {}) {
    const headers = { ...opts.headers };
    if (state.token) headers['Authorization'] = `Bearer ${state.token}`;
    if (opts.body && typeof opts.body === 'object') {
        headers['Content-Type'] = 'application/json';
        opts.body = JSON.stringify(opts.body);
    }
    const r = await fetch(path, { ...opts, headers });
    if (r.status === 401) {
        clearToken();
        if (!state.authFailed) {
            state.authFailed = true;
            showAuth('Session expired \u2014 enter token again');
        }
        throw new Error('unauthorized');
    }
    return r;
}

async function safeJSON(resp) {
    if (!resp.ok) {
        let msg = `Server error (${resp.status})`;
        try { const body = await resp.json(); if (body.error) msg = body.error; } catch {}
        throw new Error(msg);
    }
    return await resp.json();
}

// ─── UI Sections ───
const $ = id => document.getElementById(id);
function showAuth(err) {
    $('auth-section').style.display = 'flex';
    $('app-section').style.display = 'none';
    const e = $('auth-error');
    if (err) { e.textContent = err; e.classList.remove('hidden'); } else e.classList.add('hidden');
}
async function showApp() {
    $('auth-section').style.display = 'none';
    $('app-section').style.display = 'flex';
    try {
        const st = await safeJSON(await api('/api/status'));
        if (st.transport) state.transport = st.transport;
        debugLog(`Server ${st.platform || ''} ${st.version || ''} transport=${state.transport}`);
    } catch {}
    // Auto-switch to Sessions tab if server has sessions but browser has none
    try {
        const resp = await api('/api/sessions');
        const serverSessions = await safeJSON(resp);
        if (serverSessions.length > 0 && state.sessions.size === 0) {
            state.pickerMode = 'sessions';
            document.querySelectorAll('.mode-btn').forEach(b => b.classList.remove('active'));
            document.querySelector('.mode-btn[data-mode="sessions"]')?.classList.add('active');
        }
    } catch {}
    showPicker(true);
}
async function validateToken(t) {
    state.token = t;
    for (let attempt = 0; attempt < 2; attempt++) {
        try {
            debugLog(`Validating token (attempt ${attempt + 1})...`);
            const controller = new AbortController();
            const timeout = setTimeout(() => controller.abort(), 5000);
            const r = await fetch('/api/sessions', {
                headers: { 'Authorization': `Bearer ${t}` },
                signal: controller.signal,
            });
            clearTimeout(timeout);
            debugLog('Response status: ' + r.status);
            if (r.ok) {
                state.authFailed = false;
                storeToken(t, $('remember-check').checked);
                showApp();
                return true;
            }
            if (r.status === 401) return 'Invalid token';
            return 'Server error (' + r.status + ')';
        } catch (e) {
            debugLog('Attempt ' + (attempt + 1) + ' error: ' + e.name + ' ' + e.message);
            if (e.name === 'AbortError') {
                if (attempt === 0) { await new Promise(r => setTimeout(r, 1000)); continue; }
                return 'Connection timed out. Check your network.';
            }
            if (attempt === 0) { await new Promise(r => setTimeout(r, 1000)); continue; }
            return 'Connection failed: ' + e.message;
        }
    }
    return 'Connection failed after retries';
}

function debugLog(msg) {
    console.log('[WC] ' + msg);

    let pill = document.getElementById('debug-log-pill');
    let panel = document.getElementById('debug-log-panel');

    if (!pill) {
        // Create floating pill button
        pill = document.createElement('button');
        pill.id = 'debug-log-pill';
        pill.innerHTML = '<span class="debug-dot hidden" id="debug-dot"></span>&gt;_';
        document.body.appendChild(pill);

        // Create log panel
        panel = document.createElement('div');
        panel.id = 'debug-log-panel';
        panel.className = 'hidden';
        document.body.appendChild(panel);

        // Prevent session unfocus on interaction
        pill.addEventListener('mousedown', e => e.stopPropagation());
        panel.addEventListener('mousedown', e => e.stopPropagation());

        // Toggle panel on click
        pill.addEventListener('click', e => {
            e.stopPropagation();
            panel.classList.toggle('hidden');
            if (!panel.classList.contains('hidden')) {
                requestAnimationFrame(() => { panel.scrollTop = panel.scrollHeight; });
                const dot = document.getElementById('debug-dot');
                if (dot) dot.classList.add('hidden');
                pill.classList.remove('has-messages');
            }
            try { localStorage.setItem('wc_debug', panel.classList.contains('hidden') ? '0' : '1'); } catch {}
        });

        // Restore saved state (default: collapsed)
        try {
            if (localStorage.getItem('wc_debug') === '1') panel.classList.remove('hidden');
        } catch {}

        // Auto-collapse in fullscreen
        const collapseInFullscreen = () => {
            if (document.fullscreenElement || document.webkitFullscreenElement) {
                panel.classList.add('hidden');
            }
        };
        document.addEventListener('fullscreenchange', collapseInFullscreen);
        document.addEventListener('webkitfullscreenchange', collapseInFullscreen);
    }

    // Append message efficiently
    panel.insertAdjacentHTML('beforeend', msg + '<br>');
    panel.scrollTop = panel.scrollHeight;

    // Cap at ~200 messages (each message = text node + <br>)
    while (panel.childNodes.length > 400) {
        panel.removeChild(panel.firstChild);
        if (panel.firstChild) panel.removeChild(panel.firstChild);
    }

    // Show dot indicator when panel is hidden
    if (panel.classList.contains('hidden')) {
        const dot = document.getElementById('debug-dot');
        if (dot) dot.classList.remove('hidden');
        pill.classList.add('has-messages');
    }
}

// ─── Picker ───
function showPicker(isFirstLoad) {
    const overlay = $('picker-overlay');
    overlay.classList.remove('hidden');
    $('picker-close').classList.toggle('hidden', state.sessions.size === 0 && isFirstLoad && state.pickerMode !== 'sessions');
    loadPicker();
}
function hidePicker() { $('picker-overlay').classList.add('hidden'); }

async function loadPicker() {
    const content = $('picker-content');
    content.innerHTML = '<p style="color:var(--text-dim);padding:20px">Loading...</p>';
    try {
        const [winResult, appResult, sessResult] = await Promise.allSettled([api('/api/windows'), api('/api/apps'), api('/api/sessions')]);
        let windowGroups = [], installedApps = [], serverSessions = [];
        if (winResult.status === 'fulfilled') try { windowGroups = await safeJSON(winResult.value); } catch {}
        if (appResult.status === 'fulfilled') try { installedApps = await safeJSON(appResult.value); } catch {}
        if (sessResult.status === 'fulfilled') try { serverSessions = await safeJSON(sessResult.value); } catch {}
        // Update sessions badge
        const sessBtn = document.querySelector('.mode-btn[data-mode="sessions"]');
        if (sessBtn) sessBtn.textContent = `Sessions (${serverSessions.length})`;
        await renderPicker(windowGroups, installedApps, serverSessions);
    } catch (e) {
        content.innerHTML = `<p class="error">Failed to load: ${esc(e.message)}</p>`;
        const retryBtn = document.createElement('button');
        retryBtn.textContent = 'Retry';
        retryBtn.style.cssText = 'margin:10px auto;display:block;padding:6px 16px;border:1px solid var(--accent);border-radius:4px;background:transparent;color:var(--accent);cursor:pointer';
        retryBtn.addEventListener('click', () => loadPicker());
        content.appendChild(retryBtn);
    }
}

async function renderPicker(windowGroups, installedApps, serverSessions) {
    const content = $('picker-content');
    content.innerHTML = '';

    if (state.pickerMode === 'sessions') {
        renderSessionsList(content, serverSessions || []);
        return;
    }

    if (state.pickerMode === 'app') {
        // Running apps with windows
        if (windowGroups.length > 0) {
            const title = document.createElement('div');
            title.className = 'picker-section-title';
            title.textContent = 'Running Apps';
            content.appendChild(title);

            for (const g of windowGroups) {
                const card = document.createElement('div');
                card.className = 'picker-app-card';
                card.innerHTML = `
                    <img src="${g.iconUrl}" alt="" onerror="this.style.display='none'">
                    <div class="app-info">
                        <div class="app-name">${esc(g.appName)}</div>
                        <div class="app-windows">${g.windows.length} window${g.windows.length !== 1 ? 's' : ''}</div>
                    </div>
                    <span class="badge">${g.windows.length}</span>
                `;
                card.addEventListener('click', () => streamApp(g));
                content.appendChild(card);
            }
        }

        // Installed apps (launch)
        if (installedApps.length > 0) {
            const title = document.createElement('div');
            title.className = 'picker-section-title';
            title.textContent = 'Launch App';
            content.appendChild(title);
            for (const app of installedApps.slice(0, 50)) {
                const card = document.createElement('div');
                card.className = 'picker-app-card';
                card.innerHTML = `
                    <img src="/api/icons/${app.bundleID}" alt="" onerror="this.style.display='none'">
                    <div class="app-info"><div class="app-name">${esc(app.name)}</div><div class="app-windows">Not running</div></div>
                `;
                card.addEventListener('click', () => launchAndStream(app.bundleID, app.name));
                content.appendChild(card);
            }
        }
    } else {
        // By Window — flat list
        for (const g of windowGroups) {
            const title = document.createElement('div');
            title.className = 'picker-section-title';
            title.textContent = g.appName;
            content.appendChild(title);
            for (const w of g.windows) {
                const item = document.createElement('div');
                item.className = 'picker-window-item';
                item.innerHTML = `
                    <img src="${g.iconUrl}" alt="" style="width:20px;height:20px;border-radius:3px" onerror="this.style.display='none'">
                    <span class="win-title">${esc(w.title)}</span>
                    <button class="stream-btn">Stream</button>
                `;
                item.querySelector('.stream-btn').addEventListener('click', (e) => {
                    e.stopPropagation();
                    streamWindow(w.windowID, w.title, g.appName, g.iconUrl, g.bundleID);
                });
                content.appendChild(item);
            }
        }
    }

    if (state.pickerMode === 'display') {
        content.innerHTML = '<p style="color:var(--text-dim);padding:20px">Loading displays...</p>';
        try {
            const resp = await api('/api/displays');
            const displays = await safeJSON(resp);
            content.innerHTML = '';
            const title = document.createElement('div');
            title.className = 'picker-section-title';
            title.textContent = 'Displays';
            content.appendChild(title);

            for (const d of displays) {
                const card = document.createElement('div');
                card.className = 'picker-app-card';
                card.innerHTML = `
                    <div style="font-size:28px;width:32px;text-align:center">&#9783;</div>
                    <div class="app-info">
                        <div class="app-name">${esc(d.name)}</div>
                        <div class="app-windows">${d.width} &times; ${d.height}</div>
                    </div>
                    <span class="badge">Desktop</span>
                `;
                card.addEventListener('click', () => streamDisplay(d.displayID, d.name));
                content.appendChild(card);
            }
            if (displays.length === 0) {
                content.innerHTML = '<p style="color:var(--text-dim);padding:20px;text-align:center">No displays found</p>';
            }
        } catch (e) {
            content.innerHTML = `<p class="error" style="padding:20px">Failed to load displays: ${esc(e.message)}</p>`;
        }
        return;
    }

    if (windowGroups.length === 0 && state.pickerMode === 'window') {
        content.innerHTML = '<p style="color:var(--text-dim);padding:20px;text-align:center">No windows found</p>';
    }
}

// ─── Session Management ───
function renderSessionsList(content, sessions) {
    // Count header
    const countDiv = document.createElement('div');
    countDiv.className = 'session-count';
    countDiv.textContent = `${sessions.length} / 6 sessions active`;
    content.appendChild(countDiv);

    if (sessions.length === 0) {
        const empty = document.createElement('p');
        empty.style.cssText = 'color:var(--text-dim);padding:20px;text-align:center';
        empty.textContent = 'No active sessions. Use the other tabs to create new sessions.';
        content.appendChild(empty);
        return;
    }

    for (const s of sessions) {
        const card = document.createElement('div');
        card.className = 'picker-app-card';

        // Icon
        let iconHtml;
        if (s.isDisplay) {
            iconHtml = '<div style="font-size:28px;width:32px;text-align:center">&#9783;</div>';
        } else {
            iconHtml = `<img src="/api/icons/${s.appBundleID}" alt="" style="width:32px;height:32px;border-radius:6px" onerror="this.style.display='none'">`;
        }

        // Status dot class
        const statusClass = s.status === 'connected' ? 'connected' : s.status === 'connecting' ? 'connecting' : 'disconnected';

        // Determine action button
        const localSession = state.sessions.get(s.id);
        const isLocalHealthy = localSession && localSession.status !== 'disconnected';
        let actionBtn;
        if (isLocalHealthy) {
            actionBtn = `<button class="session-switch-btn" data-action="switch" data-id="${s.id}">Switch</button>`;
        } else {
            actionBtn = `<button class="session-reconnect-btn" data-action="reconnect" data-id="${s.id}">Reconnect</button>`;
        }

        card.innerHTML = `
            ${iconHtml}
            <div class="app-info">
                <div class="app-name">
                    <span class="status-dot ${statusClass}" style="display:inline-block;vertical-align:middle;margin-right:6px"></span>
                    ${esc(s.windowTitle)}
                </div>
                <div class="app-windows">${s.width} &times; ${s.height} &mdash; ${s.isDisplay ? 'Desktop' : 'Window'}</div>
            </div>
            <div class="session-card-actions">
                ${actionBtn}
                <button class="session-close-btn" data-action="close" data-id="${s.id}">Close</button>
            </div>
        `;

        // Wire action buttons
        card.addEventListener('click', async (e) => {
            const btn = e.target.closest('button');
            if (!btn) return;
            e.stopPropagation();
            const action = btn.dataset.action;
            const id = btn.dataset.id;
            if (action === 'switch') {
                hidePicker();
                switchTo(id);
            } else if (action === 'reconnect') {
                await reconnectSession(s);
            } else if (action === 'close') {
                await closeSessionFromList(id);
            }
        });

        content.appendChild(card);
    }
}

async function reconnectSession(s) {
    // Clean up dead local state if exists
    if (state.sessions.has(s.id)) {
        const existing = state.sessions.get(s.id);
        if (existing.pc) existing.pc.close();
        if (existing.ws) existing.ws.close();
        if (existing._cleanup) existing._cleanup();
        if (existing._stallCheck) clearInterval(existing._stallCheck);
        if (existing._iceRestartTimer) clearTimeout(existing._iceRestartTimer);
        if (existing._disconnectTimer) clearTimeout(existing._disconnectTimer);
        if (existing._pollIceInterval) clearInterval(existing._pollIceInterval);
        if (existing._resizeObserver) existing._resizeObserver.disconnect();
        existing.view.remove();
        state.sessions.delete(s.id);
        removeTab(s.id);
    }
    hidePicker();
    const iconUrl = s.isDisplay ? null : `/api/icons/${s.appBundleID}`;
    try {
        await connectSession(s.id, s.windowTitle, s.appBundleID, iconUrl, s.appBundleID, s.isDisplay);
    } catch (e) {
        showToast('Session no longer exists');
        showPicker(false);
    }
}

async function closeSessionFromList(sessionId) {
    if (state.sessions.has(sessionId)) {
        await closeSession(sessionId);
    } else {
        try {
            await api(`/api/sessions/${sessionId}/delete`, { method: 'POST' });
        } catch (e) {
            // Session may already be gone — ignore
        }
    }
    loadPicker();
}

// ─── Streaming ───
async function streamApp(group) {
    if (group.windows.length === 1) {
        // Single window — stream it directly
        await streamWindow(group.windows[0].windowID, group.windows[0].title, group.appName, group.iconUrl, group.bundleID);
    } else {
        // Multiple windows — expand to show individual windows so user can pick
        renderAppWindows(group);
    }
}

function renderAppWindows(group) {
    const content = $('picker-content');
    content.innerHTML = '';
    const back = document.createElement('div');
    back.className = 'picker-section-title';
    back.style.cursor = 'pointer';
    back.innerHTML = `&larr; Back &mdash; ${esc(group.appName)} (${group.windows.length} windows)`;
    back.addEventListener('click', loadPicker);
    content.appendChild(back);

    for (const w of group.windows) {
        const item = document.createElement('div');
        item.className = 'picker-window-item';
        item.innerHTML = `
            <img src="${group.iconUrl}" alt="" style="width:20px;height:20px;border-radius:3px" onerror="this.style.display='none'">
            <span class="win-title">${esc(w.title)}</span>
            <button class="stream-btn">Stream</button>
        `;
        item.querySelector('.stream-btn').addEventListener('click', e => {
            e.stopPropagation();
            streamWindow(w.windowID, w.title, group.appName, group.iconUrl, group.bundleID);
        });
        content.appendChild(item);
    }
}

function showSessionsTab() {
    state.pickerMode = 'sessions';
    document.querySelectorAll('.mode-btn').forEach(b => b.classList.remove('active'));
    document.querySelector('.mode-btn[data-mode="sessions"]')?.classList.add('active');
    showPicker(false);
}

async function streamDisplay(displayID, displayName) {
    try {
        const resp = await api('/api/sessions/create/display', { method: 'POST', body: { displayID } });
        if (resp.status === 429) { showSessionsTab(); return; }
        const text = await resp.text();
        let info;
        try { info = JSON.parse(text); } catch { alert(`Server error: ${text || resp.statusText}`); return; }
        if (info.error) { alert(`Error: ${info.error}`); return; }
        if (!info.id) { alert(`Invalid response from server`); return; }
        hidePicker();
        await connectSession(info.id, displayName, 'Desktop', null, 'display', true);
    } catch (e) { alert(`Failed: ${e.message}`); }
}

async function streamWindow(windowID, title, appName, iconUrl, bundleID) {
    try {
        const resp = await api('/api/sessions/create', { method: 'POST', body: { windowID } });
        if (resp.status === 429) { showSessionsTab(); return; }
        const text = await resp.text();
        let info;
        try { info = JSON.parse(text); } catch { alert(`Server error: ${text || resp.statusText}`); return; }
        if (info.error) { alert(`Error: ${info.error}`); return; }
        if (!info.id) { alert(`Invalid response from server`); return; }
        hidePicker();
        await connectSession(info.id, title, appName, iconUrl, bundleID, false);
    } catch (e) { alert(`Failed: ${e.message}`); }
}

async function launchAndStream(bundleID, appName) {
    try {
        await api('/api/launch', { method: 'POST', body: { bundleID } });
        // Wait briefly for the app to create a window, then refresh picker
        setTimeout(() => loadPicker(), 2000);
    } catch (e) { alert(`Launch failed: ${e.message}`); }
}

// ─── ICE Restart ───
async function iceRestart(ss) {
    if (ss._iceRestartInProgress) return false;
    ss._iceRestartInProgress = true;
    try {
        debugLog('ICE restart attempt for session ' + ss.id);
        const offer = await ss.pc.createOffer({ iceRestart: true });
        await ss.pc.setLocalDescription(offer);
        const resp = await api(`/api/sessions/${ss.id}/offer`, {
            method: 'POST', body: { sdp: offer.sdp, iceRestart: true }
        });
        const answer = await safeJSON(resp);
        if (answer.error) throw new Error(answer.error);
        await ss.pc.setRemoteDescription(new RTCSessionDescription({ type: 'answer', sdp: answer.sdp }));
        if (ss._pollIceInterval) clearInterval(ss._pollIceInterval);
        startIcePolling(ss);
        ss._iceRestartCount = 0;
        debugLog('ICE restart succeeded');
        return true;
    } catch (e) {
        debugLog('ICE restart failed: ' + e.message);
        ss._iceRestartCount = (ss._iceRestartCount || 0) + 1;
        return false;
    } finally {
        ss._iceRestartInProgress = false;
    }
}

async function attemptIceRestart(ss) {
    if (!state.sessions.has(ss.id)) return;
    if (ss.pc.iceConnectionState === 'connected' || ss.pc.iceConnectionState === 'completed') return;
    if (ss._iceRestartInProgress) {
        ss._iceRestartTimer = setTimeout(() => attemptIceRestart(ss), 2000);
        return;
    }
    const ok = await iceRestart(ss);
    if (ok) return;
    if (!state.sessions.has(ss.id)) return;
    if ((ss._iceRestartCount || 0) >= 3) {
        debugLog('ICE restart exhausted, full reconnect');
        reconnectSession(ss);
        return;
    }
    const delay = Math.min(2000 * (ss._iceRestartCount || 1), 8000);
    ss._iceRestartTimer = setTimeout(() => attemptIceRestart(ss), delay);
}

function startIcePolling(ss) {
    let polls = 0;
    ss._pollIceInterval = setInterval(async () => {
        try {
            const r = await api(`/api/sessions/${ss.id}/ice/get`);
            const cands = await safeJSON(r);
            for (const c of cands)
                await ss.pc.addIceCandidate(new RTCIceCandidate({
                    candidate: c.candidate, sdpMLineIndex: c.sdpMLineIndex, sdpMid: c.sdpMid }));
        } catch {}
        if (++polls > 60 || ss.pc.connectionState === 'connected' || ss.pc.connectionState === 'failed')
            clearInterval(ss._pollIceInterval);
    }, 500);
}

async function connectSession(sessionId, title, appName, iconUrl, bundleID, isDisplay = false) {
    if (typeof RTCPeerConnection === 'undefined' || state.transport !== 'webrtc') {
        debugLog('Using WebSocket transport (' + state.transport + ')');
        await connectSessionWS(sessionId, title, appName, iconUrl, bundleID, isDisplay);
        return;
    }
    debugLog('WebRTC available, using WebRTC path');
    const pc = new RTCPeerConnection({
        iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
        iceCandidatePoolSize: 2,
    });
    const inputDC = pc.createDataChannel('input', { ordered: true });

    // Add recvonly transceivers so the SDP offer includes video/audio media sections.
    // Without this, the server can't send video — the answer must match the offer's media lines.
    pc.addTransceiver('video', { direction: 'recvonly' });
    pc.addTransceiver('audio', { direction: 'recvonly' });

    // Create session view
    const view = createSessionView(sessionId);
    const video = view.querySelector('video');
    const canvas = view.querySelector('canvas');
    const statusOverlay = view.querySelector('.conn-status');

    const ss = {
        id: sessionId, pc, inputDC, video, canvas, view,
        title, appName, iconUrl, bundleID, isDisplay,
        status: 'connecting', controlDC: null,
    };
    state.sessions.set(sessionId, ss);
    addTab(ss);
    switchTo(sessionId);

    // Hide window resize controls for display sessions
    if (isDisplay) {
        const wc = view.querySelector('.win-controls');
        if (wc) wc.style.display = 'none';
    }

    statusOverlay.textContent = 'Signaling...';

    // Tracks
    pc.ontrack = e => {
        console.log('Got track:', e.track.kind, e.track.readyState);
        statusOverlay.textContent = 'Track received, waiting for frames...';
        video.srcObject = e.streams[0] || new MediaStream([e.track]);
        video.onplaying = () => { statusOverlay.style.display = 'none'; };
    };

    // Control channel
    pc.ondatachannel = e => {
        if (e.channel.label === 'control') {
            ss.controlDC = e.channel;
            e.channel.onmessage = msg => handleControl(sessionId, msg.data);
        }
    };

    // ICE
    pc.onicecandidate = e => {
        if (e.candidate) {
            api(`/api/sessions/${sessionId}/ice/send`, {
                method: 'POST',
                body: { candidate: e.candidate.candidate, sdpMLineIndex: e.candidate.sdpMLineIndex, sdpMid: e.candidate.sdpMid },
            }).catch(() => {});
        }
    };

    pc.oniceconnectionstatechange = () => {
        debugLog('ICE state: ' + pc.iceConnectionState);
        if (pc.iceConnectionState === 'failed') {
            if (ss._disconnectTimer) clearTimeout(ss._disconnectTimer);
            if (ss._iceRestartTimer) clearTimeout(ss._iceRestartTimer);
            ss._iceRestartTimer = setTimeout(() => attemptIceRestart(ss), 1000);
        } else if (pc.iceConnectionState === 'disconnected') {
            statusOverlay.textContent = 'Reconnecting...';
            statusOverlay.style.display = '';
            ss._disconnectTimer = setTimeout(() => {
                if (pc.iceConnectionState !== 'connected') attemptIceRestart(ss);
            }, 5000);
        } else if (pc.iceConnectionState === 'connected') {
            if (ss._iceRestartTimer) clearTimeout(ss._iceRestartTimer);
            if (ss._disconnectTimer) clearTimeout(ss._disconnectTimer);
            ss._iceRestartCount = 0;
            statusOverlay.style.display = 'none';
        }
    };

    pc.onconnectionstatechange = () => {
        debugLog('Connection state: ' + pc.connectionState);
        ss.status = pc.connectionState === 'connected' ? 'connected'
            : pc.connectionState === 'failed' ? 'disconnected' : 'connecting';
        updateTab(sessionId);
        if (pc.connectionState === 'connected') {
            statusOverlay.textContent = 'Connected, waiting for video...';
            $('toolbar').classList.remove('hidden');
            if (!ss._inputCaptureSetup) {
                ss._inputCaptureSetup = true;
                setupInputCapture(ss);
            }
            // Frame arrival monitor — only create once
            if (!ss._stallCheck && 'requestVideoFrameCallback' in HTMLVideoElement.prototype) {
                let lastFrameAt = performance.now();
                const checkFrame = (now, metadata) => {
                    lastFrameAt = now;
                    const stall = ss.view.querySelector('.stall-overlay');
                    if (stall) stall.remove();
                    video.requestVideoFrameCallback(checkFrame);
                };
                video.requestVideoFrameCallback(checkFrame);
                ss._stallCheck = setInterval(() => {
                    if (state.activeId !== ss.id) return;
                    if (ss._iceRestartInProgress) return;
                    if (performance.now() - lastFrameAt > 8000 && ss.status === 'connected') {
                        if (!ss.view.querySelector('.stall-overlay')) {
                            const overlay = document.createElement('div');
                            overlay.className = 'stall-overlay';
                            overlay.innerHTML = '<div>Stream stalled<br><button class="reconnect-stall-btn">Reconnect</button></div>';
                            overlay.querySelector('.reconnect-stall-btn').addEventListener('click', () => {
                                overlay.remove();
                                reconnectSession(ss);
                            });
                            ss.view.appendChild(overlay);
                        }
                    }
                }, 3000);
            }
        } else if (pc.connectionState === 'failed' && !ss._iceRestartTimer) {
            // Backup trigger if iceConnectionState events didn't fire
            ss._iceRestartTimer = setTimeout(() => attemptIceRestart(ss), 1000);
        }
    };

    // Offer/Answer
    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    const resp = await api(`/api/sessions/${sessionId}/offer`, { method: 'POST', body: { sdp: offer.sdp } });
    let answer;
    try {
        answer = await safeJSON(resp);
    } catch (e) {
        statusOverlay.textContent = 'Signaling failed: ' + e.message;
        ss.status = 'disconnected';
        updateTab(sessionId);
        pc.close();
        return;
    }
    if (answer.error) { console.error('Offer error:', answer.error); return; }
    await pc.setRemoteDescription(new RTCSessionDescription({ type: 'answer', sdp: answer.sdp }));

    // Poll ICE candidates (30s, reusable for ICE restart)
    startIcePolling(ss);
}

// ─── WebSocket JPEG Fallback ───
async function connectSessionWS(sessionId, title, appName, iconUrl, bundleID, isDisplay) {
    const view = createSessionView(sessionId);
    const video = view.querySelector('video');
    const canvas = view.querySelector('canvas');
    const statusOverlay = view.querySelector('.conn-status');

    // Replace <video> with <img> for JPEG frames
    const img = document.createElement('img');
    img.style.cssText = 'flex:1;min-height:0;width:100%;object-fit:contain;background:#000';
    video.replaceWith(img);

    const ss = {
        id: sessionId, pc: null, inputDC: null, video: img, canvas, view,
        title, appName, iconUrl, bundleID, isDisplay,
        status: 'connecting', controlDC: null, ws: null,
    };
    state.sessions.set(sessionId, ss);
    addTab(ss);
    switchTo(sessionId);

    if (isDisplay) {
        const wc = view.querySelector('.win-controls');
        if (wc) wc.style.display = 'none';
    }

    statusOverlay.textContent = 'Connecting via WebSocket...';

    // Connect WebSocket
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${proto}//${location.host}/ws/stream/${sessionId}?token=${state.token}`;
    const ws = new WebSocket(wsUrl);
    ws.binaryType = 'blob';
    ss.ws = ws;

    let pendingRender = false;

    ws.onopen = () => {
        debugLog('WebSocket connected for session ' + sessionId);
        ss.status = 'connected';
        updateTab(sessionId);
        statusOverlay.textContent = 'Connected, waiting for frames...';
        $('toolbar').classList.remove('hidden');
        setupInputCaptureWS(ss, img, canvas);
    };

    ws.onmessage = (e) => {
        if (typeof e.data === 'string') { handleControl(sessionId, e.data); return; }
        if (e.data instanceof Blob && !pendingRender) {
            pendingRender = true;
            const url = URL.createObjectURL(e.data);
            img.onload = () => {
                URL.revokeObjectURL(url);
                pendingRender = false;
                if (statusOverlay.style.display !== 'none') {
                    statusOverlay.style.display = 'none';
                }
            };
            img.src = url;
        }
    };

    ws.onclose = () => {
        debugLog('WebSocket closed for session ' + sessionId);
        ss.status = 'disconnected';
        updateTab(sessionId);
    };

    ws.onerror = (e) => {
        debugLog('WebSocket error: ' + e.type);
        statusOverlay.textContent = 'WebSocket connection failed';
    };
}

function setupInputCaptureWS(ss, img, canvas) {
    // Resize canvas to match image
    const resizeCanvas = () => {
        const vw = img.naturalWidth || 1;
        const vh = img.naturalHeight || 1;
        const container = ss.view;
        const cw = container.clientWidth || window.innerWidth;
        const ch = container.clientHeight || window.innerHeight;
        const videoAspect = vw / vh;
        const containerAspect = cw / ch;

        let renderW, renderH, offsetX, offsetY;
        if (videoAspect > containerAspect) {
            renderW = cw; renderH = cw / videoAspect;
            offsetX = 0; offsetY = (ch - renderH) / 2;
        } else {
            renderH = ch; renderW = ch * videoAspect;
            offsetX = (cw - renderW) / 2; offsetY = 0;
        }

        canvas.width = cw;
        canvas.height = ch;
        canvas.style.width = cw + 'px';
        canvas.style.height = ch + 'px';
        canvas.style.position = 'absolute';
        canvas.style.top = '0';
        canvas.style.left = '0';
        canvas.style.zIndex = '2';
        canvas._render = { renderW, renderH, offsetX, offsetY };
    };

    resizeCanvas();
    img.addEventListener('load', resizeCanvas);
    ss._resizeObserver = new ResizeObserver(resizeCanvas);
    ss._resizeObserver.observe(ss.view);

    function normalize(clientX, clientY) {
        const rect = canvas.getBoundingClientRect();
        const cx = clientX - rect.left;
        const cy = clientY - rect.top;
        const r = canvas._render;
        if (!r) return null;
        const nx = Math.max(0, Math.min(1, (cx - r.offsetX) / r.renderW));
        const ny = Math.max(0, Math.min(1, (cy - r.offsetY) / r.renderH));
        return { x: nx, y: ny };
    }

    function mods(e) {
        return {
            shiftKey: e.shiftKey || state.modifiers.shift,
            ctrlKey: e.ctrlKey || state.modifiers.ctrl,
            altKey: e.altKey || state.modifiers.alt,
            metaKey: e.metaKey || state.modifiers.meta,
        };
    }

    function wsSend(msg) {
        if (ss.ws && ss.ws.readyState === WebSocket.OPEN) {
            ss.ws.send(JSON.stringify(msg));
        }
    }

    // Mouse events
    let mouseMoveThrottle = 0;
    canvas.addEventListener('mousedown', e => {
        e.preventDefault();
        state.focused = true;
        ss.view.classList.add('focused');
        const pt = normalize(e.clientX, e.clientY);
        if (pt) wsSend({ type: 'mousedown', ...pt, button: e.button, clickCount: e.detail, ...mods(e) });
    });
    canvas.addEventListener('mouseup', e => {
        const pt = normalize(e.clientX, e.clientY);
        if (pt) wsSend({ type: 'mouseup', ...pt, button: e.button, ...mods(e) });
    });
    canvas.addEventListener('mousemove', e => {
        const now = performance.now();
        if (now - mouseMoveThrottle < 16) return;
        mouseMoveThrottle = now;
        const pt = normalize(e.clientX, e.clientY);
        if (pt) wsSend({ type: 'mousemove', ...pt, buttons: e.buttons, ...mods(e) });
    });
    canvas.addEventListener('contextmenu', e => e.preventDefault());
    canvas.addEventListener('wheel', e => {
        e.preventDefault();
        const pt = normalize(e.clientX, e.clientY);
        if (!pt) return;
        const deltaY = Math.round(e.deltaY / 40) || (e.deltaY > 0 ? 1 : -1);
        const deltaX = Math.round(e.deltaX / 40) || 0;
        wsSend({ type: 'wheel', ...pt, deltaX, deltaY, ...mods(e) });
    }, { passive: false });

    // Touch events
    if ('ontouchstart' in window || navigator.maxTouchPoints > 0) {
        let touchStart = null;
        let touchTimer = null;

        canvas.addEventListener('touchstart', e => {
            if (e.touches.length !== 1) return;
            const t = e.touches[0];
            touchStart = { x: t.clientX, y: t.clientY, time: Date.now() };
            touchTimer = setTimeout(() => {
                const pt = normalize(touchStart.x, touchStart.y);
                if (pt) {
                    wsSend({ type: 'mousedown', ...pt, button: 1, clickCount: 1, ...mods(e) });
                    wsSend({ type: 'mouseup', ...pt, button: 1, ...mods(e) });
                }
                touchStart = null;
            }, 500);
        });

        canvas.addEventListener('touchmove', e => {
            if (!touchStart) return;
            const t = e.touches[0];
            if (Math.abs(t.clientX - touchStart.x) > 10 || Math.abs(t.clientY - touchStart.y) > 10) {
                clearTimeout(touchTimer);
                touchStart = null;
            }
        });

        canvas.addEventListener('touchend', e => {
            clearTimeout(touchTimer);
            if (!touchStart) return;
            if (Date.now() - touchStart.time < 300) {
                const pt = normalize(touchStart.x, touchStart.y);
                if (pt) {
                    wsSend({ type: 'mousedown', ...pt, button: 0, clickCount: 1, ...mods(e) });
                    wsSend({ type: 'mouseup', ...pt, button: 0, ...mods(e) });
                }
            }
            touchStart = null;
        });
    }

    // Keyboard
    const keyHandler = e => {
        if (!state.focused || state.activeId !== ss.id) return;
        if (e.target.tagName === 'INPUT' && e.target.id !== 'hidden-input') return;
        e.preventDefault();
        wsSend({ type: e.type === 'keydown' ? 'keydown' : 'keyup', key: e.key, code: e.code, ...mods(e), repeat: e.repeat });
    };
    document.addEventListener('keydown', keyHandler);
    document.addEventListener('keyup', keyHandler);
    ss._cleanup = () => {
        document.removeEventListener('keydown', keyHandler);
        document.removeEventListener('keyup', keyHandler);
    };
}

async function closeSession(sessionId) {
    const ss = state.sessions.get(sessionId);
    if (!ss) return;
    if (ss.pc) ss.pc.close();
    if (ss.ws) ss.ws.close();
    if (ss._cleanup) ss._cleanup();
    if (ss._stallCheck) clearInterval(ss._stallCheck);
    if (ss._iceRestartTimer) clearTimeout(ss._iceRestartTimer);
    if (ss._disconnectTimer) clearTimeout(ss._disconnectTimer);
    if (ss._pollIceInterval) clearInterval(ss._pollIceInterval);
    if (ss._resizeObserver) ss._resizeObserver.disconnect();
    ss.view.remove();
    state.sessions.delete(sessionId);
    removeTab(sessionId);
    api(`/api/sessions/${sessionId}/delete`).catch(() => {});

    if (state.activeId === sessionId) {
        const next = state.sessions.keys().next().value;
        if (next) switchTo(next);
        else { state.activeId = null; $('toolbar').classList.add('hidden'); showPicker(true); }
    }
}

function handleControl(sessionId, data) {
    try {
        const msg = JSON.parse(data);
        const ss = state.sessions.get(sessionId);
        if (!ss) return;
        if (msg.type === 'meta') { ss.title = msg.title; updateTab(sessionId); }
        else if (msg.type === 'resize') { /* handled by video element auto-fit */ }
        else if (msg.type === 'error') {
            let overlay = ss.view.querySelector('.overlay');
            if (!overlay) { overlay = document.createElement('div'); overlay.className = 'overlay'; ss.view.appendChild(overlay); }
            overlay.textContent = '';
            const div = document.createElement('div');
            div.textContent = msg.reason;
            const br = document.createElement('br');
            const btn = document.createElement('button');
            btn.textContent = 'Remove';
            btn.style.cssText = 'margin-top:12px;padding:6px 20px;border:1px solid var(--accent);border-radius:4px;background:transparent;color:var(--accent);cursor:pointer';
            btn.addEventListener('click', () => closeSession(sessionId));
            div.appendChild(br);
            div.appendChild(btn);
            overlay.appendChild(div);
        }
    } catch {}
}

// ─── Session View ───
function createSessionView(sessionId) {
    const view = document.createElement('div');
    view.className = 'session-view';
    view.id = `sv-${sessionId}`;
    view.innerHTML = `
        <video autoplay playsinline muted></video>
        <canvas></canvas>
        <div class="focus-ring"></div>
        <div class="conn-status">Connecting...</div>
        <button class="fullscreen-btn" title="Toggle fullscreen">&#9974;</button>
        <div class="win-controls">
            <button data-action="maximize" title="Maximize">&#9744; Max</button>
            <button data-action="half-left" title="Half Left">&#9699; Left</button>
            <button data-action="half-right" title="Half Right">&#9698; Right</button>
            <button data-action="custom-size" title="Custom Size">&#8693; Size</button>
        </div>
        <div class="size-popover hidden">
            <label>Width <input type="number" class="size-w" value="1280" min="400" max="5120" step="10"></label>
            <label>Height <input type="number" class="size-h" value="800" min="300" max="2880" step="10"></label>
            <button class="size-apply">Apply</button>
        </div>
    `;
    // Wire window control buttons
    view.querySelector('.win-controls').addEventListener('click', async (e) => {
        const btn = e.target.closest('button');
        if (!btn) return;
        const action = btn.dataset.action;
        try {
            if (action === 'maximize') {
                await api(`/api/sessions/${sessionId}/maximize`, { method: 'POST' });
            } else if (action === 'half-left') {
                await api(`/api/sessions/${sessionId}/half`, { method: 'POST', body: { left: true } });
            } else if (action === 'half-right') {
                await api(`/api/sessions/${sessionId}/half`, { method: 'POST', body: { left: false } });
            } else if (action === 'custom-size') {
                const popover = view.querySelector('.size-popover');
                popover.classList.toggle('hidden');
                // Pre-fill with current size from session info
                const ss = state.sessions.get(sessionId);
                if (ss && ss.windowWidth) {
                    popover.querySelector('.size-w').value = ss.windowWidth;
                    popover.querySelector('.size-h').value = ss.windowHeight;
                }
            }
        } catch (err) { console.error('Window control error:', err); }
    });
    // Wire custom size apply
    view.querySelector('.size-apply').addEventListener('click', async () => {
        const w = parseInt(view.querySelector('.size-w').value) || 1280;
        const h = parseInt(view.querySelector('.size-h').value) || 800;
        view.querySelector('.size-popover').classList.add('hidden');
        try {
            await api(`/api/sessions/${sessionId}/resize`, { method: 'POST', body: { width: w, height: h } });
        } catch (err) { console.error('Resize error:', err); }
    });
    // Wire fullscreen button
    view.querySelector('.fullscreen-btn').addEventListener('click', e => {
        e.stopPropagation();
        toggleFullscreen();
    });

    $('viewer-container').appendChild(view);
    return view;
}

// ─── Tab Bar ───
function addTab(ss) {
    const tab = document.createElement('div');
    tab.className = 'tab';
    tab.id = `tab-${ss.id}`;
    tab.innerHTML = `
        ${ss.iconUrl ? `<img src="${ss.iconUrl}" alt="" onerror="this.style.display='none'">` : ''}
        <span class="status-dot connecting"></span>
        <span class="tab-title">${esc(ss.title)}</span>
        <span class="close-tab">&times;</span>
    `;
    tab.addEventListener('click', e => {
        if (e.target.classList.contains('close-tab')) { closeSession(ss.id); return; }
        switchTo(ss.id);
    });
    $('tabs').appendChild(tab);
}

function removeTab(sessionId) {
    const tab = $(`tab-${sessionId}`);
    if (tab) tab.remove();
}

function updateTab(sessionId) {
    const ss = state.sessions.get(sessionId);
    if (!ss) return;
    const tab = $(`tab-${sessionId}`);
    if (!tab) return;
    tab.querySelector('.tab-title').textContent = ss.title;
    const dot = tab.querySelector('.status-dot');
    dot.className = `status-dot ${ss.status}`;
}

function switchTo(sessionId) {
    // Deactivate old
    if (state.activeId && state.activeId !== sessionId) {
        const old = state.sessions.get(state.activeId);
        if (old) {
            old.view.classList.remove('active', 'focused');
            $(`tab-${state.activeId}`)?.classList.remove('active');
            sendInput(old, { type: 'deactivate' });
        }
    }

    state.activeId = sessionId;
    const ss = state.sessions.get(sessionId);
    if (!ss) return;

    ss.view.classList.add('active');
    $(`tab-${sessionId}`)?.classList.add('active');
    sendInput(ss, { type: 'activate' });

    // Activate on server
    api(`/api/sessions/${sessionId}/activate`).catch(() => {});
}

// ─── Input Capture ───
function sendInput(ss, msg) {
    if (ss.inputDC && ss.inputDC.readyState === 'open') {
        ss.inputDC.send(JSON.stringify(msg));
    }
}

function setupInputCapture(ss) {
    const canvas = ss.canvas;
    const video = ss.video;

    // Resize canvas to match video rendered area
    const resizeCanvas = () => {
        const vw = video.videoWidth || 1;
        const vh = video.videoHeight || 1;
        const container = ss.view;
        const cw = container.clientWidth || video.clientWidth || window.innerWidth;
        const ch = container.clientHeight || video.clientHeight || window.innerHeight;
        const videoAspect = vw / vh;
        const containerAspect = cw / ch;

        let renderW, renderH, offsetX, offsetY;
        if (videoAspect > containerAspect) {
            renderW = cw; renderH = cw / videoAspect;
            offsetX = 0; offsetY = (ch - renderH) / 2;
        } else {
            renderH = ch; renderW = ch * videoAspect;
            offsetX = (cw - renderW) / 2; offsetY = 0;
        }

        canvas.width = cw;
        canvas.height = ch;
        canvas.style.width = cw + 'px';
        canvas.style.height = ch + 'px';
        // Make canvas cover the full container so it receives all click events
        canvas.style.position = 'absolute';
        canvas.style.top = '0';
        canvas.style.left = '0';
        canvas.style.zIndex = '2';
        canvas._render = { renderW, renderH, offsetX, offsetY };
    };

    // Initialize canvas immediately (don't wait for video to load)
    resizeCanvas();
    video.addEventListener('loadedmetadata', resizeCanvas);
    video.addEventListener('resize', resizeCanvas);
    video.addEventListener('playing', resizeCanvas);
    ss._resizeObserver = new ResizeObserver(resizeCanvas);
    ss._resizeObserver.observe(ss.view);

    // Normalize coordinates to [0,1] within the video content area
    function normalize(clientX, clientY) {
        const rect = canvas.getBoundingClientRect();
        const cx = clientX - rect.left;
        const cy = clientY - rect.top;
        const r = canvas._render;
        if (!r) return null;
        // Clamp to [0, 1] instead of rejecting out-of-bounds.
        // This ensures the cursor can reach screen edges (needed for Dock, menu bar, etc.)
        const nx = Math.max(0, Math.min(1, (cx - r.offsetX) / r.renderW));
        const ny = Math.max(0, Math.min(1, (cy - r.offsetY) / r.renderH));
        return { x: nx, y: ny };
    }

    function mods(e) {
        return {
            shiftKey: e.shiftKey || state.modifiers.shift,
            ctrlKey: e.ctrlKey || state.modifiers.ctrl,
            altKey: e.altKey || state.modifiers.alt,
            metaKey: e.metaKey || state.modifiers.meta,
        };
    }

    // Mouse events
    let mouseMoveThrottle = 0;
    canvas.addEventListener('mousedown', e => {
        e.preventDefault();
        state.focused = true;
        ss.view.classList.add('focused');
        const pt = normalize(e.clientX, e.clientY);
        debugLog(`mousedown at (${e.clientX},${e.clientY}) -> norm=(${pt?.x?.toFixed(3)},${pt?.y?.toFixed(3)}) btn=${e.button} detail=${e.detail}`);
        if (pt) sendInput(ss, { type: 'mousedown', ...pt, button: e.button, clickCount: e.detail, ...mods(e) });
    });
    canvas.addEventListener('mouseup', e => {
        const pt = normalize(e.clientX, e.clientY);
        if (pt) sendInput(ss, { type: 'mouseup', ...pt, button: e.button, ...mods(e) });
    });
    canvas.addEventListener('mousemove', e => {
        const now = performance.now();
        if (now - mouseMoveThrottle < 16) return; // ~60fps
        mouseMoveThrottle = now;
        const pt = normalize(e.clientX, e.clientY);
        if (pt) sendInput(ss, { type: 'mousemove', ...pt, buttons: e.buttons, ...mods(e) });
    });
    canvas.addEventListener('contextmenu', e => { e.preventDefault(); });
    canvas.addEventListener('wheel', e => {
        e.preventDefault();
        const pt = normalize(e.clientX, e.clientY);
        if (!pt) return;
        // Normalize to line units
        const deltaY = Math.round(e.deltaY / 40) || (e.deltaY > 0 ? 1 : -1);
        const deltaX = Math.round(e.deltaX / 40) || 0;
        sendInput(ss, { type: 'wheel', ...pt, deltaX, deltaY, ...mods(e) });
    }, { passive: false });

    // Touch events (all touch-capable devices, not just mobile)
    if ('ontouchstart' in window || navigator.maxTouchPoints > 0) {
        let touchStart = null;
        let touchTimer = null;

        canvas.addEventListener('touchstart', e => {
            if (e.touches.length !== 1) return;
            const t = e.touches[0];
            touchStart = { x: t.clientX, y: t.clientY, time: Date.now() };
            touchTimer = setTimeout(() => {
                // Long press -> right click
                const pt = normalize(touchStart.x, touchStart.y);
                if (pt) {
                    sendInput(ss, { type: 'mousedown', ...pt, button: 1, clickCount: 1, ...mods(e) });
                    sendInput(ss, { type: 'mouseup', ...pt, button: 1, ...mods(e) });
                }
                touchStart = null;
            }, 500);
        });

        canvas.addEventListener('touchmove', e => {
            if (!touchStart) return;
            const t = e.touches[0];
            const dx = Math.abs(t.clientX - touchStart.x);
            const dy = Math.abs(t.clientY - touchStart.y);
            if (dx > 10 || dy > 10) {
                clearTimeout(touchTimer);
                touchStart = null;
            }
        });

        canvas.addEventListener('touchend', e => {
            clearTimeout(touchTimer);
            if (!touchStart) return;
            const elapsed = Date.now() - touchStart.time;
            if (elapsed < 300) {
                const pt = normalize(touchStart.x, touchStart.y);
                if (pt) {
                    sendInput(ss, { type: 'mousedown', ...pt, button: 0, clickCount: 1, ...mods(e) });
                    sendInput(ss, { type: 'mouseup', ...pt, button: 0, ...mods(e) });
                }
            }
            touchStart = null;
        });
    }

    // Keyboard
    const keyHandler = e => {
        if (!state.focused || state.activeId !== ss.id) return;
        // Don't capture if typing in an input
        if (e.target.tagName === 'INPUT' && e.target.id !== 'hidden-input') return;

        e.preventDefault();
        const msg = {
            type: e.type === 'keydown' ? 'keydown' : 'keyup',
            key: e.key, code: e.code,
            ...mods(e), repeat: e.repeat,
        };
        sendInput(ss, msg);
    };
    document.addEventListener('keydown', keyHandler);
    document.addEventListener('keyup', keyHandler);

    // Store cleanup refs
    ss._cleanup = () => {
        document.removeEventListener('keydown', keyHandler);
        document.removeEventListener('keyup', keyHandler);
    };
}

// ─── Toolbar ───
function setupToolbar() {
    const toolbar = $('toolbar');

    toolbar.addEventListener('click', e => {
        const btn = e.target.closest('button');
        if (!btn) return;
        e.preventDefault();

        const ss = state.sessions.get(state.activeId);
        if (!ss) return;

        // Modifier toggle
        const mod = btn.dataset.mod;
        if (mod) {
            state.modifiers[mod] = !state.modifiers[mod];
            btn.classList.toggle('active', state.modifiers[mod]);
            return;
        }

        // Special key
        const key = btn.dataset.key;
        if (key) {
            const m = {
                shiftKey: state.modifiers.shift,
                ctrlKey: state.modifiers.ctrl,
                altKey: state.modifiers.alt,
                metaKey: state.modifiers.meta,
            };
            sendInput(ss, { type: 'keydown', key, code: key, ...m, repeat: false });
            sendInput(ss, { type: 'keyup', key, code: key });

            // Clear modifiers after use (one-shot)
            for (const k of Object.keys(state.modifiers)) state.modifiers[k] = false;
            toolbar.querySelectorAll('.mod-btn').forEach(b => b.classList.remove('active'));
            return;
        }

        // Keyboard toggle
        if (btn.id === 'kb-toggle') {
            const hi = $('hidden-input');
            if (document.activeElement === hi) { hi.blur(); }
            else { hi.focus(); }
        }
    });

    // Hidden input for soft keyboard
    const hi = $('hidden-input');
    hi.addEventListener('input', e => {
        const ss = state.sessions.get(state.activeId);
        if (!ss || !e.data) return;
        sendInput(ss, { type: 'text', chars: e.data });
        hi.value = '';
    });
    hi.addEventListener('keydown', e => {
        if (e.key === 'Enter') {
            const ss = state.sessions.get(state.activeId);
            if (ss) {
                sendInput(ss, { type: 'keydown', key: 'Enter', code: 'Enter', repeat: false });
                sendInput(ss, { type: 'keyup', key: 'Enter', code: 'Enter' });
            }
            e.preventDefault();
        }
    });
}

// ─── Picker Mode Toggle ───
function setupPickerModes() {
    const modes = $('picker-modes');
    modes.addEventListener('click', e => {
        const btn = e.target.closest('.mode-btn');
        if (!btn) return;
        modes.querySelectorAll('.mode-btn').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        state.pickerMode = btn.dataset.mode;
        loadPicker();
    });
}

// ─── Helpers ───
function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

// ─── Fullscreen ───
function toggleFullscreen() {
    if (state.isFullscreen) {
        exitFullscreen();
    } else {
        hidePicker();
        document.body.classList.add('fullscreen');
        state.isFullscreen = true;
        try {
            const el = document.documentElement;
            if (el.requestFullscreen) el.requestFullscreen();
            else if (el.webkitRequestFullscreen) el.webkitRequestFullscreen();
        } catch {}
    }
}

function exitFullscreen() {
    document.body.classList.remove('fullscreen');
    state.isFullscreen = false;
    try {
        if (document.fullscreenElement) document.exitFullscreen();
        else if (document.webkitFullscreenElement) document.webkitExitFullscreen();
    } catch {}
}

// Sync if browser exits fullscreen externally (Esc in browser chrome, swipe, etc.)
document.addEventListener('fullscreenchange', () => {
    if (!document.fullscreenElement) {
        document.body.classList.remove('fullscreen');
        state.isFullscreen = false;
    }
});
document.addEventListener('webkitfullscreenchange', () => {
    if (!document.webkitFullscreenElement) {
        document.body.classList.remove('fullscreen');
        state.isFullscreen = false;
    }
});

// Global Escape handler for fullscreen (runs BEFORE per-session handlers)
document.addEventListener('keydown', e => {
    if (e.code === 'Escape' && state.isFullscreen) {
        e.preventDefault();
        e.stopImmediatePropagation();
        if (!state.escapeForwardPending) {
            exitFullscreen();
            state.escapeForwardPending = true;
            showToast('Press Escape again to send to remote');
            setTimeout(() => { state.escapeForwardPending = false; }, 2000);
        } else {
            state.escapeForwardPending = false;
            const ss = state.sessions.get(state.activeId);
            if (ss) {
                sendInput(ss, { type: 'keydown', key: 'Escape', code: 'Escape', repeat: false });
                sendInput(ss, { type: 'keyup', key: 'Escape', code: 'Escape' });
            }
        }
    }
}, true); // useCapture: true

function showToast(msg) {
    const el = document.createElement('div');
    el.className = 'toast';
    el.textContent = msg;
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 2200);
}

// ─── Init ───
document.addEventListener('DOMContentLoaded', async () => {
    // Check secure context — WebRTC requires HTTPS on mobile browsers
    if (!window.isSecureContext && location.hostname !== 'localhost' && location.hostname !== '127.0.0.1') {
        if (location.hostname.endsWith('.ts.net')) {
            // Auto-redirect to HTTPS (Tailscale Serve on port 443)
            const httpsUrl = location.href.replace(/^http:/, 'https:').replace(/:\d+/, '');
            location.href = httpsUrl;
            return;
        }
    }

    // Update service worker (force new version, clear old caches)
    if ('serviceWorker' in navigator) {
        navigator.serviceWorker.register('/sw.js').then(reg => {
            reg.update(); // Force check for new SW immediately
        }).catch(() => {});
    }

    setupToolbar();
    setupPickerModes();

    $('add-tab-btn').addEventListener('click', () => showPicker(false));
    $('picker-close').addEventListener('click', hidePicker);

    // Click outside viewer unfocuses
    document.addEventListener('mousedown', e => {
        if (!e.target.closest('.session-view')) {
            state.focused = false;
            document.querySelectorAll('.session-view').forEach(v => v.classList.remove('focused'));
        }
    });

    // Auth
    const loginBtn = $('login-btn');
    const tokenInput = $('token-input');

    loginBtn.addEventListener('click', async () => {
        const t = tokenInput.value.trim();
        if (!t) { showAuth('Please enter a token'); return; }
        loginBtn.disabled = true; loginBtn.textContent = 'Connecting...';
        try {
            const result = await validateToken(t);
            if (typeof result === 'string') showAuth(result);
        } catch (e) {
            showAuth('Unexpected error: ' + e.message);
        }
        loginBtn.disabled = false; loginBtn.textContent = 'Connect';
    });
    tokenInput.addEventListener('keydown', e => { if (e.key === 'Enter') loginBtn.click(); });

    // Auto-connect (token required on all origins when Tailscale is active)
    const existing = getToken();
    if (existing) {
        const result = await validateToken(existing);
        if (typeof result === 'string') showAuth(result);
    } else {
        showAuth();
    }
});
