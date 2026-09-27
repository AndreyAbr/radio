// admin.js - Клиентская логика для панели управления сервером и студийного микшера прямого эфира

let activeStationForUpload = null;
let currentStationsList = [];

// ============================================================================
// Утилиты форматирования
// ============================================================================

function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}

function formatUptime(timespan) {
    if (!timespan) return '00:00:00';
    if (typeof timespan === 'string') {
        return timespan.split('.')[0];
    }
    if (typeof timespan === 'object' && timespan.totalSeconds !== undefined) {
        const totalSecs = Math.floor(timespan.totalSeconds || 0);
        const hrs = Math.floor(totalSecs / 3600);
        const mins = Math.floor((totalSecs % 3600) / 60);
        const secs = totalSecs % 60;
        return `${hrs.toString().padStart(2, '0')}:${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
    }
    return String(timespan);
}

function formatTrackTime(seconds) {
    if (isNaN(seconds) || seconds <= 0) return '00:00';
    const mins = Math.floor(seconds / 60);
    const secs = Math.floor(seconds % 60);
    return `${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
}

// ============================================================================
// Загрузка данных телеметрии и станций
// ============================================================================

async function fetchAdminData() {
    try {
        const res = await fetch('/api/admin/stats');
        if (!res.ok) throw new Error(`HTTP error ${res.status}`);
        const data = await res.json();

        // 1. Обновление карточек телеметрии
        document.getElementById('valUptime').textContent = formatUptime(data.uptime);
        document.getElementById('valListeners').textContent = data.totalListeners ?? 0;
        document.getElementById('valTraffic').textContent = formatBytes(data.totalBytesSent);
        document.getElementById('valMemory').textContent = `${data.memoryMb ?? 0} MB`;
        document.getElementById('stationsCount').textContent = data.stationsCount ?? (data.stations ? data.stations.length : 0);

        currentStationsList = data.stations || [];

        // 2. Отрисовка станций
        renderAdminStations(currentStationsList);

        // 3. Синхронизация селектора станций в микшере
        updateMixerStationSelect(currentStationsList);

        // 4. Отрисовка логов активности
        renderLogs(data.recentLogs || []);
    } catch (err) {
        console.error('Ошибка загрузки данных админ-панели:', err);
    }
}

// ============================================================================
// Отрисовка карточек радиостанций с таймлайном и элементами управления
// ============================================================================

function renderAdminStations(stations) {
    const container = document.getElementById('adminStationsGrid');
    if (!container) return;

    // Проверяем, захвачен ли фокус каким-либо ползунком перемотки пользователем
    const activeEl = document.activeElement;
    const isUserDraggingSeek = activeEl && activeEl.classList.contains('timeline-slider');

    container.innerHTML = '';

    stations.forEach(st => {
        const card = document.createElement('div');
        card.className = 'station-admin-card';

        const trackName = st.currentTrack || 'Эфир пуст';
        const duration = st.trackDurationSeconds || 0;
        const remaining = st.trackRemainingSeconds || 0;
        const elapsed = Math.max(0, duration - remaining);

        let statusBadge = '<span class="badge-tag live">● Эфир онлайн</span>';
        if (st.isLiveDj) {
            statusBadge = '<span class="badge-tag dj">🎙️ DJ в эфире</span>';
        } else if (st.isPaused) {
            statusBadge = '<span class="badge-tag paused">⏸ На паузе</span>';
        }

        const pauseBtnText = st.isPaused ? '▶ Возобновить' : '⏸ Пауза';
        const pauseBtnClass = st.isPaused ? 'btn-action paused-btn' : 'btn-action';
        const timeDisplay = st.isPaused
            ? `${formatTrackTime(elapsed)} / ${formatTrackTime(duration)} <span style="color: var(--primary);">(Пауза)</span>`
            : `${formatTrackTime(elapsed)} / ${formatTrackTime(duration)} (осталось: ⏳ ${formatTrackTime(remaining)})`;

        card.innerHTML = `
            <div class="station-admin-header">
                <div>
                    <div class="station-admin-title">${st.name}</div>
                    <div class="station-badge-row">
                        <span class="station-admin-id">/stream/${st.id}</span>
                        ${statusBadge}
                    </div>
                </div>
                <span class="station-admin-listeners" onclick="openListenersModal('${st.id}', ${st.maxListeners ?? 0})" style="cursor: pointer;" title="${st.maxListeners ? 'Лимит: ' + st.maxListeners + '. Нажмите для настройки' : 'Без лимита. Нажмите для настройки'}">
                    👥 ${st.listeners}${st.maxListeners ? ' / ' + st.maxListeners : ''} онл.
                </span>
            </div>

            <div class="station-admin-body">
                <div class="meta-row">
                    <span class="meta-key">Текущий трек:</span>
                    <span class="meta-val" title="${trackName}">🎵 ${trackName}</span>
                </div>
                <div class="meta-row">
                    <span class="meta-key">Время:</span>
                    <span class="meta-val" style="font-weight: 600;">${timeDisplay}</span>
                </div>
                <div class="meta-row">
                    <span class="meta-key">В плейлисте:</span>
                    <span class="meta-val">${st.trackCount} треков</span>
                </div>

                <!-- Интерактивный таймлайн перемотки -->
                <div class="station-timeline">
                    <span class="timeline-time">${formatTrackTime(elapsed)}</span>
                    <input type="range" class="timeline-slider" id="slider-${st.id}" min="0" max="${Math.max(1, duration)}" value="${elapsed}" 
                        onchange="seekStation('${st.id}', this.value)" title="Нажмите для перемотки трека на сервере">
                    <span class="timeline-time">${formatTrackTime(duration)}</span>
                </div>
            </div>

            <div class="station-admin-actions">
                <button class="${pauseBtnClass}" onclick="togglePauseStation('${st.id}')" title="Поставить эфир на паузу или продолжить">${pauseBtnText}</button>
                <button class="btn-action" onclick="seekStationRelative('${st.id}', -15)" title="Перемотать на 15 секунд назад">⏪ -15 с</button>
                <button class="btn-action" onclick="seekStationRelative('${st.id}', 15)" title="Перемотать на 15 секунд вперед">⏩ +15 с</button>
                <button class="btn-action" onclick="skipTrack('${st.id}')" title="Переключить на следующий трек">⏭️ След.</button>
                <button class="btn-action" onclick="openTracksModal('${st.id}')" title="Просмотр и удаление треков">📑 Треки (${st.trackCount})</button>
                <button class="btn-action" onclick="openListenersModal('${st.id}', ${st.maxListeners ?? 0})" title="Настройка лимита слушателей (503)">👥 Лимит</button>
                <button class="btn-action" onclick="reloadPlaylist('${st.id}')" title="Пересканировать папку на диске">🔄 Обновить</button>
                <button class="btn-action" onclick="triggerUpload('${st.id}')" title="Загрузить MP3/OGG файлы">📤 Загрузить MP3</button>
                <a href="/stream/${st.id}" target="_blank" class="btn-action" title="Открыть прямой поток в новой вкладке">🎧 Поток</a>
            </div>
        `;
        container.appendChild(card);
    });

    if (isUserDraggingSeek && activeEl) {
        const restored = document.getElementById(activeEl.id);
        if (restored) restored.focus();
    }
}

function updateMixerStationSelect(stations) {
    const select = document.getElementById('mixerStationSelect');
    if (!select || !stations.length) return;

    const currentVal = select.value || 'all';

    // Проверяем состав списка станций (сравнение множеств без сброса активного выбора)
    const existingStationIds = Array.from(select.options).map(o => o.value).filter(v => v !== 'all');
    const newStationIds = stations.map(s => s.id);
    const sortedExisting = [...existingStationIds].sort();
    const sortedNew = [...newStationIds].sort();
    const isSame = sortedExisting.length === sortedNew.length && sortedExisting.every((id, idx) => id === sortedNew[idx]);

    if (!isSame) {
        select.innerHTML = '<option value="all">📡 Все станции (Общий эфир)</option>';
        stations.forEach(st => {
            const opt = document.createElement('option');
            opt.value = st.id;
            opt.textContent = `${st.name} (${st.id})`;
            select.appendChild(opt);
        });

        if (currentVal && Array.from(select.options).some(o => o.value === currentVal)) {
            select.value = currentVal;
        } else {
            select.value = 'all';
        }
    }
}

async function onMixerStationChange() {
    const select = document.getElementById('mixerStationSelect');
    if (!select) return;
    const targetStationId = select.value || 'all';

    // Если DJ в прямом эфире, динамически переподключаем поток на выбранную станцию
    if (isOnAir) {
        console.log('Переключение станции прямого эфира на:', targetStationId);
        try {
            if (djSocket) {
                try { djSocket.close(); } catch { }
                djSocket = null;
            }

            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            djSocket = new WebSocket(`${protocol}//${window.location.host}/ws/stations/${targetStationId}/live-dj`);
            djSocket.binaryType = 'arraybuffer';

            await new Promise((resolve, reject) => {
                djSocket.onopen = () => resolve();
                djSocket.onerror = (e) => reject(new Error('Не удалось переподключить WebSocket к станции ' + targetStationId));
            });

            const targetDesc = targetStationId === 'all'
                ? 'Все станции'
                : (currentStationsList.find(s => s.id === targetStationId)?.name || targetStationId);
            document.getElementById('micStatusLabel').textContent = `Микрофон в эфире (${targetDesc})`;
        } catch (err) {
            console.error('Ошибка смены станции прямого эфира:', err);
            stopLiveDj();
            alert('Ошибка смены станции вещания: ' + err.message);
        }
    }
}

// Отрисовка таблицы активности
function renderLogs(logs) {
    const tbody = document.getElementById('logTableBody');
    if (!tbody) return;

    if (!logs || logs.length === 0) {
        tbody.innerHTML = `<tr><td colspan="4" style="text-align: center; color: var(--text-secondary);">Событий пока нет</td></tr>`;
        return;
    }

    tbody.innerHTML = logs.map(l => {
        const timeStr = new Date(l.timestamp).toLocaleTimeString();
        return `
            <tr>
                <td>${timeStr}</td>
                <td class="log-ip">${l.ip}</td>
                <td class="log-station">${l.station}</td>
                <td>${l.action}</td>
            </tr>
        `;
    }).join('');
}

// ============================================================================
// Серверные действия управления воспроизведением (Пауза, Перемотка, Скип)
// ============================================================================

async function togglePauseStation(id) {
    try {
        const res = await fetch(`/api/admin/stations/${id}/toggle-pause`, { method: 'POST' });
        const json = await res.json();
        if (!res.ok) throw new Error(json.message || 'Ошибка переключения паузы');
        fetchAdminData();
    } catch (err) {
        alert('Ошибка при изменении состояния паузы: ' + err.message);
    }
}

async function seekStation(id, targetSeconds) {
    try {
        const res = await fetch(`/api/admin/stations/${id}/seek`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ seconds: parseFloat(targetSeconds) })
        });
        const json = await res.json();
        if (!res.ok) throw new Error(json.message || 'Ошибка перемотки трека');
        fetchAdminData();
    } catch (err) {
        alert('Ошибка при перемотке: ' + err.message);
    }
}

async function seekStationRelative(id, deltaSeconds) {
    try {
        const res = await fetch(`/api/admin/stations/${id}/seek`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ delta: parseFloat(deltaSeconds) })
        });
        const json = await res.json();
        if (!res.ok) throw new Error(json.message || 'Ошибка относительной перемотки');
        fetchAdminData();
    } catch (err) {
        alert('Ошибка при перемотке: ' + err.message);
    }
}

async function skipTrack(id) {
    try {
        const res = await fetch(`/api/admin/stations/${id}/skip`, { method: 'POST' });
        const json = await res.json();
        if (!res.ok) throw new Error(json.message || 'Ошибка пропуска трека');
        fetchAdminData();
    } catch (err) {
        alert('Ошибка при переключении трека: ' + err.message);
    }
}

async function reloadPlaylist(id) {
    try {
        const res = await fetch(`/api/admin/stations/${id}/reload`, { method: 'POST' });
        const json = await res.json();
        if (!res.ok) throw new Error(json.message || 'Ошибка обновления плейлиста');
        fetchAdminData();
    } catch (err) {
        alert('Ошибка при обновлении плейлиста: ' + err.message);
    }
}

// Загрузка аудиофайлов в станцию
function triggerUpload(stationId) {
    activeStationForUpload = stationId;
    const input = document.getElementById('fileUploadInput');
    input.value = '';
    input.click();
}

document.getElementById('fileUploadInput').addEventListener('change', async (e) => {
    if (!activeStationForUpload || !e.target.files.length) return;

    const formData = new FormData();
    for (const file of e.target.files) {
        formData.append('files', file);
    }

    try {
        const res = await fetch(`/api/admin/stations/${activeStationForUpload}/upload`, {
            method: 'POST',
            body: formData
        });
        const json = await res.json();
        if (!res.ok) throw new Error(json.message || 'Ошибка загрузки файлов');
        alert(json.message);
        fetchAdminData();
    } catch (err) {
        alert('Ошибка загрузки файлов: ' + err.message);
    }
});

// Модальное окно создания станции
function openNewStationModal() {
    document.getElementById('stationModal').style.display = 'flex';
    document.getElementById('stationId').focus();
}

function closeNewStationModal() {
    document.getElementById('stationModal').style.display = 'none';
}

async function handleCreateStation(e) {
    e.preventDefault();
    const idInput = document.getElementById('stationId');
    const nameInput = document.getElementById('stationName');
    const id = idInput.value.trim();
    const name = nameInput.value.trim();

    const formData = new FormData();
    formData.append('id', id);
    formData.append('name', name);

    try {
        const res = await fetch('/api/admin/stations', {
            method: 'POST',
            body: formData
        });
        const json = await res.json();
        if (!res.ok) throw new Error(json.message || 'Ошибка создания станции');

        closeNewStationModal();
        document.getElementById('newStationForm').reset();
        fetchAdminData();
    } catch (err) {
        alert(err.message);
    }
}

// ============================================================================
// Управление треками станции (Просмотр и Удаление треков)
// ============================================================================

let activeStationForTracks = null;
let activeStationForLimit = null;

async function openTracksModal(stationId) {
    activeStationForTracks = stationId;
    const st = currentStationsList.find(s => s.id === stationId);
    const stationName = st ? st.name : stationId;
    
    document.getElementById('tracksModalStationTitle').textContent = `${stationName} (${stationId})`;
    document.getElementById('tracksModal').style.display = 'flex';
    await renderTracksModalList(stationId);
}

function closeTracksModal() {
    document.getElementById('tracksModal').style.display = 'none';
    activeStationForTracks = null;
}

function triggerUploadFromModal() {
    if (activeStationForTracks) {
        triggerUpload(activeStationForTracks);
    }
}

async function renderTracksModalList(stationId) {
    const container = document.getElementById('tracksListContainer');
    const countEl = document.getElementById('tracksModalCount');
    if (!container) return;

    container.innerHTML = '<div style="color: var(--text-secondary); padding: 12px; text-align: center;">Загрузка треков...</div>';

    try {
        const res = await fetch(`/api/admin/stations/${stationId}/tracks`);
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const data = await res.json();

        const tracks = data.tracks || [];
        const currentTrack = data.currentTrack || '';
        if (countEl) countEl.textContent = `${tracks.length} треков в плейлисте`;

        if (tracks.length === 0) {
            container.innerHTML = '<div style="color: var(--text-secondary); padding: 16px; text-align: center;">В плейлисте нет треков. Загрузите файлы .mp3 или .ogg.</div>';
            return;
        }

        container.innerHTML = tracks.map(track => {
            const isPlaying = track === currentTrack;
            const safeTrackName = track.replace(/"/g, '&quot;').replace(/'/g, '&#39;');
            return `
                <div class="track-item-row ${isPlaying ? 'active' : ''}">
                    <div class="track-info-col">
                        <span>🎵</span>
                        <span class="track-title-text" title="${safeTrackName}">${safeTrackName}</span>
                        ${isPlaying ? '<span class="track-playing-badge">В эфире</span>' : ''}
                    </div>
                    <button class="btn-delete-track" onclick="handleDeleteTrack('${stationId}', '${encodeURIComponent(track)}')">
                        🗑️ Удалить
                    </button>
                </div>
            `;
        }).join('');
    } catch (err) {
        container.innerHTML = `<div style="color: #ef4444; padding: 12px; text-align: center;">Ошибка загрузки списка треков: ${err.message}</div>`;
    }
}

async function handleDeleteTrack(stationId, encodedFileName) {
    const fileName = decodeURIComponent(encodedFileName);
    if (!confirm(`Удалить трек "${fileName}" с сервера?`)) {
        return;
    }

    try {
        const res = await fetch(`/api/admin/stations/${stationId}/tracks/${encodeURIComponent(fileName)}`, {
            method: 'DELETE'
        });
        const json = await res.json();
        if (!res.ok) throw new Error(json.message || 'Ошибка удаления трека');

        await renderTracksModalList(stationId);
        fetchAdminData();
    } catch (err) {
        alert('Ошибка при удалении трека: ' + err.message);
    }
}

// ============================================================================
// Настройка ограничения слушателей (503 Service Unavailable)
// ============================================================================

function openListenersModal(stationId, currentLimit) {
    activeStationForLimit = stationId;
    const st = currentStationsList.find(s => s.id === stationId);
    const stationName = st ? st.name : stationId;

    document.getElementById('listenersModalStationTitle').textContent = `${stationName} (${stationId})`;
    const input = document.getElementById('maxListenersInput');
    input.value = currentLimit > 0 ? currentLimit : '';
    document.getElementById('listenersModal').style.display = 'flex';
    input.focus();
}

function closeListenersModal() {
    document.getElementById('listenersModal').style.display = 'none';
    activeStationForLimit = null;
}

async function handleSetMaxListeners(e) {
    e.preventDefault();
    if (!activeStationForLimit) return;

    const input = document.getElementById('maxListenersInput');
    const val = parseInt(input.value.trim(), 10);
    const maxListeners = (val > 0) ? val : null;

    try {
        const res = await fetch(`/api/admin/stations/${activeStationForLimit}/max-listeners`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ maxListeners: maxListeners })
        });
        const json = await res.json();
        if (!res.ok) throw new Error(json.message || 'Ошибка обновления лимита');

        closeListenersModal();
        fetchAdminData();
    } catch (err) {
        alert('Ошибка установки лимита слушателей: ' + err.message);
    }
}

// ============================================================================
// Студийный DJ-микшер прямого эфира (Web Audio API + LAME MP3 + WebSocket)
// ============================================================================

let isOnAir = false;
let isTestingMic = false;
let djSocket = null;
let audioCtx = null;
let micStream = null;
let micSource = null;
let micGainNode = null;
let musicGainNode = null;
let masterGainNode = null;
let micAnalyser = null;
let musicAnalyser = null;
let masterAnalyser = null;
let scriptProcessor = null;
let mp3Encoder = null;
let mixerAudioElem = null;

let userMusicVolume = 0.8;
let userMicVolume = 1.0;
let isMusicMuted = false;
let isMicMuted = false;
let duckingEnabled = true;
let duckingDepthRatio = 0.25; // снижать до 25% при речи
let animationFrameId = null;

// Универсальный метод захвата микрофона с поддержкой всех браузеров и fallback
async function requestMicrophoneStream() {
    const banner = document.getElementById('micHelpBanner');
    const bannerText = document.getElementById('micHelpText');
    const bannerIcon = document.getElementById('micHelpIcon');

    // 1. Проверяем, запущен ли браузер в безопасном контексте (Secure Context)
    const isLocal = window.location.hostname === 'localhost' || 
                    window.location.hostname === '127.0.0.1' || 
                    window.location.hostname === '::1' ||
                    window.location.hostname === '[::1]' ||
                    window.location.protocol === 'https:' ||
                    Boolean(window.isSecureContext);

    if (!isLocal && !window.isSecureContext) {
        const err = 'Браузер блокирует доступ к микрофону при открытии через незащищенный HTTP на внешнем IP. Пожалуйста, откройте панель управления строго через http://localhost:5000/admin или http://127.0.0.1:5000/admin';
        setMicBannerState('error', '🚫 Ошибка безопасности браузера', err);
        throw new Error(err);
    }

    setMicBannerState('waiting', '⏳ Ожидание разрешения...', 'Вверху страницы появилось окно браузера с вопросом: <em>«Разрешить сайту использовать микрофон?»</em> Нажмите <strong>«Разрешить» (Allow)</strong>.');

    // 2. Доступ к API getUserMedia
    const nav = navigator;
    let stream = null;

    if (nav.mediaDevices && nav.mediaDevices.getUserMedia) {
        try {
            // Попытка 1: С аппаратным эхоподавлением и шумоподавлением
            stream = await nav.mediaDevices.getUserMedia({
                audio: {
                    echoCancellation: true,
                    noiseSuppression: true,
                    autoGainControl: true
                }
            });
        } catch (initialErr) {
            console.warn('Initial mic request with constraints failed, retrying simple {audio: true}:', initialErr);
            // Попытка 2: Простой запрос без ограничений
            try {
                stream = await nav.mediaDevices.getUserMedia({ audio: true });
            } catch (fallbackErr) {
                handleMicError(fallbackErr);
                throw fallbackErr;
            }
        }
    } else {
        // Fallback на старые браузеры
        const legacyGUM = nav.getUserMedia || nav.webkitGetUserMedia || nav.mozGetUserMedia || nav.msGetUserMedia;
        if (legacyGUM) {
            stream = await new Promise((resolve, reject) => {
                legacyGUM.call(nav, { audio: true }, resolve, reject);
            }).catch(e => {
                handleMicError(e);
                throw e;
            });
        } else {
            const err = 'Ваш браузер не поддерживает захват звука (Web Audio / MediaDevices). Используйте современный Chrome, Edge или Firefox.';
            setMicBannerState('error', '❌ Браузер не поддерживается', err);
            throw new Error(err);
        }
    }

    setMicBannerState('success', '✅ Микрофон подключен!', 'Доступ получен успешно. Индикатор микрофона теперь реагирует на ваш голос.');
    return stream;
}

function handleMicError(err) {
    console.error('Ошибка доступа к микрофону:', err);
    if (err.name === 'NotAllowedError' || err.name === 'PermissionDeniedError') {
        setMicBannerState('error', '🚫 Доступ к микрофону запрещен', 
            'Вы или ваш браузер заблокировали микрофон для этого сайта.<br>' +
            '<strong>Как исправить:</strong> Нажмите на значок 🔒 или ⚙️ слева от адреса <code>http://localhost:5000</code> в строке браузера, переведите пункт <strong>«Микрофон» в положение «Разрешить»</strong> и перезагрузите страницу.');
    } else if (err.name === 'NotFoundError' || err.name === 'DevicesNotFoundError') {
        setMicBannerState('error', '🎧 Микрофон не найден', 
            'Компьютер не видит подключенных микрофонов. Проверьте подключение гарнитуры или микрофона в настройках Windows.');
    } else if (err.name === 'NotReadableError' || err.name === 'TrackStartError') {
        setMicBannerState('error', '⚠️ Микрофон занят', 
            'Микрофон уже используется другой программой (Zoom, Skype, Discord, Teams). Закройте их и повторите попытку.');
    } else {
        setMicBannerState('error', 'Ошибка микрофона', err.message || String(err));
    }
}

function setMicBannerState(state, title, message) {
    const banner = document.getElementById('micHelpBanner');
    const bannerText = document.getElementById('micHelpText');
    const bannerIcon = document.getElementById('micHelpIcon');
    if (!banner || !bannerText) return;

    banner.className = 'mic-help-banner ' + (state === 'success' ? 'success' : state === 'error' ? 'error' : '');
    bannerIcon.textContent = state === 'success' ? '🎤' : state === 'error' ? '🚫' : state === 'waiting' ? '⏳' : '💡';
    bannerText.innerHTML = `<strong>${title}</strong><br>${message}`;
}

// Тестирование микрофона без выхода в эфир
async function testMicrophone() {
    if (isTestingMic) {
        stopMicTest();
        return;
    }

    try {
        const stream = await requestMicrophoneStream();
        micStream = stream;
        isTestingMic = true;

        audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 44100 });
        micSource = audioCtx.createMediaStreamSource(micStream);
        micGainNode = audioCtx.createGain();
        micGainNode.gain.setValueAtTime(userMicVolume, audioCtx.currentTime);

        micAnalyser = audioCtx.createAnalyser();
        micAnalyser.fftSize = 256;
        micSource.connect(micGainNode);
        micGainNode.connect(micAnalyser);

        document.getElementById('btnTestMic').textContent = '⏹ Остановить тест';
        document.getElementById('btnTestMic').classList.add('paused-btn');
        document.getElementById('micStatusLabel').textContent = 'Идет тест (говорите в микрофон)...';

        startMixerVULoop();
    } catch (err) {
        console.warn('Тест микрофона остановлен из-за ошибки:', err);
    }
}

function stopMicTest() {
    isTestingMic = false;
    if (animationFrameId) {
        cancelAnimationFrame(animationFrameId);
        animationFrameId = null;
    }
    if (micStream) {
        micStream.getTracks().forEach(t => t.stop());
        micStream = null;
    }
    if (audioCtx) {
        try { audioCtx.close(); } catch { }
        audioCtx = null;
    }
    document.getElementById('btnTestMic').textContent = '🎧 Проверить микрофон';
    document.getElementById('btnTestMic').classList.remove('paused-btn');
    document.getElementById('micStatusLabel').textContent = 'Микрофон не подключен';
    document.getElementById('micMeterFill').style.width = '0%';
}

// Включение / Выключение прямого эфира ведущего
async function toggleLiveDj() {
    if (isTestingMic) {
        stopMicTest();
    }

    if (isOnAir) {
        stopLiveDj();
    } else {
        await startLiveDj();
    }
}

function resampleTo44100(inputFloat32, inRate) {
    if (inRate === 44100) return inputFloat32;
    const ratio = 44100 / inRate;
    const outLength = Math.round(inputFloat32.length * ratio);
    const result = new Float32Array(outLength);
    for (let i = 0; i < outLength; i++) {
        const srcPos = i / ratio;
        const i0 = Math.floor(srcPos);
        const i1 = Math.min(i0 + 1, inputFloat32.length - 1);
        const frac = srcPos - i0;
        result[i] = inputFloat32[i0] * (1 - frac) + inputFloat32[i1] * frac;
    }
    return result;
}

async function startLiveDj() {
    const stationSelect = document.getElementById('mixerStationSelect');
    const targetStationId = (stationSelect && stationSelect.value) ? stationSelect.value : 'all';
    const currentStation = targetStationId === 'all'
        ? currentStationsList.find(s => s.currentTrack && s.currentTrack !== 'Эфир пуст')
        : currentStationsList.find(s => s.id === targetStationId);

    // 1. Запрос доступа к микрофону ведущего
    try {
        micStream = await requestMicrophoneStream();
    } catch (err) {
        return;
    }

    // 2. Подключение к серверному WebSocket прямого эфира
    try {
        const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        djSocket = new WebSocket(`${protocol}//${window.location.host}/ws/stations/${targetStationId}/live-dj`);
        djSocket.binaryType = 'arraybuffer';

        await new Promise((resolve, reject) => {
            djSocket.onopen = () => resolve();
            djSocket.onerror = (e) => reject(new Error('Не удалось подключиться к серверному сокету эфира для ' + targetStationId));
        });
    } catch (err) {
        if (micStream) micStream.getTracks().forEach(t => t.stop());
        alert('Ошибка подключения к серверу вещания: ' + err.message);
        return;
    }

    // 3. Инициализация Web Audio API графа (нативно 44100 Гц для идеального согласования с LAME)
    audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 44100 });

    // Микрофонный канал
    micSource = audioCtx.createMediaStreamSource(micStream);
    micGainNode = audioCtx.createGain();
    micGainNode.gain.setValueAtTime(isMicMuted ? 0 : userMicVolume, audioCtx.currentTime);

    micAnalyser = audioCtx.createAnalyser();
    micAnalyser.fftSize = 256;
    micSource.connect(micGainNode);
    micGainNode.connect(micAnalyser);

    // Музыкальный канал
    musicGainNode = audioCtx.createGain();
    musicGainNode.gain.setValueAtTime(isMusicMuted ? 0 : userMusicVolume, audioCtx.currentTime);

    musicAnalyser = audioCtx.createAnalyser();
    musicAnalyser.fftSize = 256;
    musicGainNode.connect(musicAnalyser);

    // Подключение текущего трека радиостанции для воспроизведения в микшере
    if (currentStation && currentStation.currentTrack && currentStation.currentTrack !== 'Эфир пуст') {
        try {
            mixerAudioElem = new Audio();
            mixerAudioElem.crossOrigin = 'anonymous';
            const stationDir = currentStation.id || 'rock';
            mixerAudioElem.src = `/audio/${stationDir}/${encodeURIComponent(currentStation.currentTrack)}`;
            mixerAudioElem.onloadedmetadata = () => {
                const duration = currentStation.trackDurationSeconds || 0;
                const remaining = currentStation.trackRemainingSeconds || 0;
                const elapsed = Math.max(0, duration - remaining);
                if (elapsed < (mixerAudioElem.duration || duration)) {
                    mixerAudioElem.currentTime = elapsed;
                }
                mixerAudioElem.play().catch(e => console.warn('Воспроизведение музыки в микшере:', e));
            };

            const musicSource = audioCtx.createMediaElementSource(mixerAudioElem);
            musicSource.connect(musicGainNode);
        } catch (e) {
            console.warn('Не удалось загрузить трек для микшера:', e);
        }
    }

    // Мастер-шина
    masterGainNode = audioCtx.createGain();
    micGainNode.connect(masterGainNode);
    musicGainNode.connect(masterGainNode);

    masterAnalyser = audioCtx.createAnalyser();
    masterAnalyser.fftSize = 256;
    masterGainNode.connect(masterAnalyser);

    // 4. LAME MP3 кодировщик в реальном времени
    if (typeof lamejs === 'undefined') {
        alert('Библиотека MP3 кодировщика lamejs не найдена на клиенте.');
        stopLiveDj();
        return;
    }

    mp3Encoder = new lamejs.Mp3Encoder(2, 44100, 128);

    // ScriptProcessor для захвата PCM-сэмплов с минимальным размером буфера (2048 сэмплов ~46 мс задержки)
    const bufferSize = 2048;
    scriptProcessor = audioCtx.createScriptProcessor(bufferSize, 2, 2);
    masterGainNode.connect(scriptProcessor);

    // Подключаем через muteNode с нулевым усилением к destination:
    // это гарантирует вызов onaudioprocess в браузере, исключая акустическую обратную связь (эхо) в динамиках
    const muteNode = audioCtx.createGain();
    muteNode.gain.setValueAtTime(0, audioCtx.currentTime);
    scriptProcessor.connect(muteNode);
    muteNode.connect(audioCtx.destination);

    scriptProcessor.onaudioprocess = (audioEvent) => {
        if (!isOnAir || !djSocket || djSocket.readyState !== WebSocket.OPEN) return;

        let leftFloat = audioEvent.inputBuffer.getChannelData(0);
        let rightFloat = audioEvent.inputBuffer.getChannelData(1);

        if (audioCtx.sampleRate !== 44100) {
            leftFloat = resampleTo44100(leftFloat, audioCtx.sampleRate);
            rightFloat = resampleTo44100(rightFloat, audioCtx.sampleRate);
        }

        const len = leftFloat.length;
        const leftInt16 = new Int16Array(len);
        const rightInt16 = new Int16Array(len);

        for (let i = 0; i < len; i++) {
            const l = Math.max(-1, Math.min(1, leftFloat[i]));
            const r = Math.max(-1, Math.min(1, rightFloat[i]));
            leftInt16[i] = l < 0 ? l * 0x8000 : l * 0x7FFF;
            rightInt16[i] = r < 0 ? r * 0x8000 : r * 0x7FFF;
        }

        const mp3Chunk = mp3Encoder.encodeBuffer(leftInt16, rightInt16);
        if (mp3Chunk.length > 0 && djSocket && djSocket.readyState === WebSocket.OPEN) {
            if (djSocket.bufferedAmount < 32768) {
                djSocket.send(mp3Chunk);
            }
        }
    };

    // 5. Обновление визуального состояния
    isOnAir = true;
    document.getElementById('btnOnAir').classList.add('active');
    document.getElementById('onAirText').textContent = '🔴 В ЭФИРЕ (ВЫКЛЮЧИТЬ)';
    document.getElementById('mixerStatusTag').textContent = '● ON AIR — МИКРОФОН В ЭФИРЕ';
    document.getElementById('mixerStatusTag').style.color = '#ef4444';

    const targetDesc = targetStationId === 'all'
        ? 'Все станции'
        : (currentStation?.name || targetStationId);
    document.getElementById('micStatusLabel').textContent = `Микрофон в эфире (${targetDesc})`;

    // 6. Запуск цикла индикаторов уровней (VU Meters) и Auto-Ducking
    startMixerVULoop();
}

function stopLiveDj() {
    isOnAir = false;

    if (animationFrameId) {
        cancelAnimationFrame(animationFrameId);
        animationFrameId = null;
    }

    if (scriptProcessor) {
        try { scriptProcessor.disconnect(); } catch { }
        scriptProcessor = null;
    }

    if (mixerAudioElem) {
        try {
            mixerAudioElem.pause();
            mixerAudioElem.removeAttribute('src');
            mixerAudioElem.load();
        } catch { }
        mixerAudioElem = null;
    }

    if (mp3Encoder && djSocket && djSocket.readyState === WebSocket.OPEN) {
        try {
            const flush = mp3Encoder.flush();
            if (flush.length > 0) djSocket.send(flush);
        } catch { }
    }
    mp3Encoder = null;

    if (djSocket) {
        try { djSocket.close(); } catch { }
        djSocket = null;
    }

    if (micStream) {
        micStream.getTracks().forEach(t => t.stop());
        micStream = null;
    }

    if (audioCtx) {
        try { audioCtx.close(); } catch { }
        audioCtx = null;
    }

    // Сброс UI индикаторов
    document.getElementById('btnOnAir').classList.remove('active');
    document.getElementById('onAirText').textContent = '🎙️ ВЫЙТИ В ЭФИР (ON AIR)';
    document.getElementById('mixerStatusTag').textContent = 'Микрофон отключен';
    document.getElementById('mixerStatusTag').style.color = 'var(--text-secondary)';
    document.getElementById('micStatusLabel').textContent = 'Микрофон не подключен';

    document.getElementById('musicMeterFill').style.width = '0%';
    document.getElementById('micMeterFill').style.width = '0%';
    document.getElementById('masterMeterFill').style.width = '0%';

    fetchAdminData();
}

// Цикл расчета VU-метров и Auto-Ducking
function startMixerVULoop() {
    const micData = new Uint8Array(micAnalyser ? micAnalyser.frequencyBinCount : 128);
    const musicData = new Uint8Array(musicAnalyser ? musicAnalyser.frequencyBinCount : 128);
    const masterData = new Uint8Array(masterAnalyser ? masterAnalyser.frequencyBinCount : 128);

    function loop() {
        if (!isOnAir && !isTestingMic) return;

        // Расчет уровня микрофона
        let micRms = 0;
        if (micAnalyser) {
            micAnalyser.getByteTimeDomainData(micData);
            let sum = 0;
            for (let i = 0; i < micData.length; i++) {
                const val = (micData[i] - 128) / 128;
                sum += val * val;
            }
            micRms = Math.sqrt(sum / micData.length);
            const micPct = Math.min(100, Math.round(micRms * 320));
            document.getElementById('micMeterFill').style.width = `${micPct}%`;
        }

        // Auto-Ducking: если ведущий говорит в микрофон, плавно приглушаем музыку
        if (isOnAir && duckingEnabled && musicGainNode && audioCtx) {
            const isSpeaking = micRms > 0.03 && !isMicMuted;
            const targetGain = isSpeaking ? (userMusicVolume * duckingDepthRatio) : (isMusicMuted ? 0 : userMusicVolume);
            // Плавное изменение громкости (скорость атаки 0.05с, спад 0.25с)
            musicGainNode.gain.setTargetAtTime(targetGain, audioCtx.currentTime, isSpeaking ? 0.05 : 0.25);
        }

        // Расчет уровня музыки
        if (musicAnalyser) {
            musicAnalyser.getByteTimeDomainData(musicData);
            let sum = 0;
            for (let i = 0; i < musicData.length; i++) {
                const val = (musicData[i] - 128) / 128;
                sum += val * val;
            }
            const musicRms = Math.sqrt(sum / musicData.length);
            const musicPct = Math.min(100, Math.round(musicRms * 250));
            document.getElementById('musicMeterFill').style.width = `${musicPct}%`;
        }

        // Расчет уровня мастер-микса
        if (masterAnalyser) {
            masterAnalyser.getByteTimeDomainData(masterData);
            let sum = 0;
            for (let i = 0; i < masterData.length; i++) {
                const val = (masterData[i] - 128) / 128;
                sum += val * val;
            }
            const masterRms = Math.sqrt(sum / masterData.length);
            const masterPct = Math.min(100, Math.round(masterRms * 250));
            document.getElementById('masterMeterFill').style.width = `${masterPct}%`;
        }

        animationFrameId = requestAnimationFrame(loop);
    }

    loop();
}

// Управление фейдерами микшера
function onMusicFaderChange(val) {
    userMusicVolume = parseInt(val, 10) / 100;
    document.getElementById('musicVolDisplay').textContent = `${val}%`;
    if (musicGainNode && audioCtx && !isMusicMuted) {
        musicGainNode.gain.setValueAtTime(userMusicVolume, audioCtx.currentTime);
    }
}

function onMicFaderChange(val) {
    userMicVolume = parseInt(val, 10) / 100;
    document.getElementById('micVolDisplay').textContent = `${val}%`;
    if (micGainNode && audioCtx && !isMicMuted) {
        micGainNode.gain.setValueAtTime(userMicVolume, audioCtx.currentTime);
    }
}

function toggleMuteMusic() {
    isMusicMuted = !isMusicMuted;
    const btn = document.getElementById('btnMuteMusic');
    if (btn) btn.classList.toggle('muted', isMusicMuted);

    if (musicGainNode && audioCtx) {
        musicGainNode.gain.setValueAtTime(isMusicMuted ? 0 : userMusicVolume, audioCtx.currentTime);
    }
}

function toggleMuteMic() {
    isMicMuted = !isMicMuted;
    const btn = document.getElementById('btnMuteMic');
    if (btn) btn.classList.toggle('muted', isMicMuted);

    if (micGainNode && audioCtx) {
        micGainNode.gain.setValueAtTime(isMicMuted ? 0 : userMicVolume, audioCtx.currentTime);
    }
}

function toggleMixerStationPause() {
    const btn = document.getElementById('btnPauseMusic');
    if (mixerAudioElem) {
        if (mixerAudioElem.paused) {
            mixerAudioElem.play();
            if (btn) { btn.textContent = '⏸ Пауза'; btn.classList.remove('paused'); }
        } else {
            mixerAudioElem.pause();
            if (btn) { btn.textContent = '▶ Возобновить'; btn.classList.add('paused'); }
        }
    }

    // Также передаем команду на сервер
    const stationSelect = document.getElementById('mixerStationSelect');
    if (stationSelect && stationSelect.value) {
        togglePauseStation(stationSelect.value);
    }
}

function seekMixerStationRelative(deltaSeconds) {
    if (mixerAudioElem && !isNaN(mixerAudioElem.currentTime)) {
        mixerAudioElem.currentTime = Math.max(0, mixerAudioElem.currentTime + deltaSeconds);
    }

    const stationSelect = document.getElementById('mixerStationSelect');
    if (stationSelect && stationSelect.value) {
        seekStationRelative(stationSelect.value, deltaSeconds);
    }
}

function onDuckingToggle(enabled) {
    duckingEnabled = enabled;
}

function onDuckingDepthChange(val) {
    duckingDepthRatio = parseInt(val, 10) / 100;
    document.getElementById('duckingDepthLabel').textContent = `${val}%`;
}

// Проверка разрешений микрофона при загрузке страницы
async function checkMicPermissionOnLoad() {
    if (navigator.permissions && navigator.permissions.query) {
        try {
            const perm = await navigator.permissions.query({ name: 'microphone' });
            updateMicBannerByPermission(perm.state);
            perm.onchange = () => updateMicBannerByPermission(perm.state);
        } catch (e) { }
    }
}

function updateMicBannerByPermission(state) {
    if (state === 'granted') {
        setMicBannerState('success', '✅ Доступ к микрофону разрешен', 'Микрофон готов к работе. Нажмите <strong>«🎙️ ВЫЙТИ В ЭФИР (ON AIR)»</strong>, чтобы говорить в трансляцию.');
    } else if (state === 'denied') {
        setMicBannerState('error', '🚫 Доступ к микрофону заблокирован браузером', 
            'Браузер блокирует микрофон для этой страницы. Нажмите на значок 🔒 или ⚙️ в левой части адресной строки браузера (перед URL) и переведите «Микрофон» в положение «Разрешить», затем обновите страницу.');
    } else if (state === 'prompt') {
        setMicBannerState('normal', '💡 Как говорить поверх музыки (Live DJ)', 
            'Нажмите кнопку <strong>«🎙️ ВЫЙТИ В ЭФИР (ON AIR)»</strong> или <strong>«🎤 Запросить микрофон»</strong>. Браузер покажет запрос на доступ к микрофону — нажмите <strong>«Разрешить» (Allow)</strong>.');
    }
}

// Автообновление каждые 2 секунды
fetchAdminData();
setInterval(fetchAdminData, 2000);
checkMicPermissionOnLoad();
