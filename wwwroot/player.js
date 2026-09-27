// player.js - Клиентская логика для радиоплеера

const audio = document.getElementById('audioPlayer');
const btnPlayPause = document.getElementById('btnPlayPause');
const playIcon = document.getElementById('playIcon');
const btnMute = document.getElementById('btnMute');
const volumeSlider = document.getElementById('volumeSlider');
const volumeValue = document.getElementById('volumeValue');
const visualizer = document.getElementById('visualizer');
const streamState = document.getElementById('streamState');
const currentStationName = document.getElementById('currentStationName');
const currentTrackTitle = document.getElementById('currentTrackTitle');
const currentStationListeners = document.getElementById('currentStationListeners');
const stationsList = document.getElementById('stationsList');
const trackTimeElapsed = document.getElementById('trackTimeElapsed');
const trackTimeRemaining = document.getElementById('trackTimeRemaining');
const trackProgressFill = document.getElementById('trackProgressFill');

let stations = [];
let currentStationId = 'rock'; // Станция по умолчанию
let isPlaying = false;
let currentTrackDuration = 0;
let currentTrackRemaining = 0;

// Функция форматирования секунд в mm:ss
function formatTime(seconds) {
    if (isNaN(seconds) || seconds < 0) return '00:00';
    const mins = Math.floor(seconds / 60);
    const secs = Math.floor(seconds % 60);
    return `${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
}

// 1. Инициализация станций и периодический опрос
async function fetchStations() {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 4000);
    try {
        const response = await fetch('/stations', { signal: controller.signal });
        clearTimeout(timeoutId);
        if (!response.ok) throw new Error('Ошибка получения списка станций');
        stations = await response.json();

        // Обновляем индикатор статуса сервера в шапке
        const statusBadge = document.getElementById('serverStatusBadge');
        const statusText = document.getElementById('serverStatusText');
        if (statusText) statusText.textContent = 'Сервер активен';
        if (statusBadge) statusBadge.classList.remove('offline');

        // Проверяем, существует ли текущая станция в списке
        if (stations.length > 0 && !stations.some(s => s.id === currentStationId)) {
            currentStationId = stations[0].id;
        }

        renderStations();
        updateNowPlayingUI();
        if (isPlaying) {
            updateControlsUI();
        }
    } catch (err) {
        clearTimeout(timeoutId);
        console.error('Ошибка опроса /stations:', err);
        const statusBadge = document.getElementById('serverStatusBadge');
        const statusText = document.getElementById('serverStatusText');
        if (statusBadge) statusBadge.classList.add('offline');
        if (statusText) statusText.textContent = 'Сервер недоступен';

        const active = stations.find(s => s.id === currentStationId);
        if (active && active.isPaused) {
            streamState.textContent = '⏸ Эфир на паузе';
        } else if (!isPlaying && !audio.src) {
            streamState.textContent = 'Сервер недоступен';
        }
    }
}

// 2. Отрисовка списка доступных станций (плавное обновление DOM без мерцания)
function renderStations() {
    const existingCards = stationsList.querySelectorAll('.station-card');
    const existingIds = Array.from(existingCards).map(c => c.dataset.id);
    const newIds = stations.map(s => s.id);

    // Если список идентификаторов совпадает, обновляем данные на месте
    const isSameList = existingIds.length === newIds.length && existingIds.every((id, idx) => id === newIds[idx]);

    if (isSameList) {
        stations.forEach(st => {
            const card = stationsList.querySelector(`.station-card[data-id="${st.id}"]`);
            if (card) {
                if (st.id === currentStationId) {
                    card.classList.add('active');
                } else {
                    card.classList.remove('active');
                }
                const trackEl = card.querySelector('.station-card-track');
                if (trackEl && trackEl.dataset.track !== st.currentTrack) {
                    trackEl.textContent = `🎵 ${st.currentTrack || 'В эфире'}`;
                    trackEl.title = st.currentTrack || '';
                    trackEl.dataset.track = st.currentTrack || '';
                }
                const listenersEl = card.querySelector('.station-listeners-count');
                if (listenersEl) {
                    listenersEl.textContent = st.listeners;
                }
            }
        });
        return;
    }

    // Иначе перестраиваем структуру списка станций
    stationsList.innerHTML = '';
    stations.forEach(st => {
        const card = document.createElement('div');
        card.className = `station-card ${st.id === currentStationId ? 'active' : ''}`;
        card.dataset.id = st.id;
        card.onclick = () => selectStation(st.id);

        card.innerHTML = `
            <div class="card-top">
                <span class="station-title">${st.name}</span>
                <span class="station-listeners-tag">👥 <span class="station-listeners-count">${st.listeners}</span></span>
            </div>
            <div class="station-card-track" data-track="${st.currentTrack || ''}" title="${st.currentTrack || ''}">🎵 ${st.currentTrack || 'В эфире'}</div>
        `;
        stationsList.appendChild(card);
    });
}

let currentTuneId = 0;
let isPlaybackStoppedByUser = false;
let lastKnownTrack = '';
let frozenElapsedSeconds = 0;

// 3. Выбор радиостанции
function selectStation(stationId) {
    if (currentStationId === stationId && isPlaying) return;

    currentStationId = stationId;
    isPlaybackStoppedByUser = false;
    renderStations();
    updateNowPlayingUI();
    tune(stationId);
}

// 4. Корректная остановка текущего аудиопотока и освобождение сокета
function stopAudio() {
    try {
        audio.pause();
        audio.removeAttribute('src');
        audio.load();
    } catch (e) { }
}

let tuneTimeout = null;

// 5. Подключение к потоку станции (Tune)
function tune(stationId) {
    const tuneId = ++currentTuneId;
    isPlaybackStoppedByUser = false;
    stopAudio();

    const active = stations.find(s => s.id === stationId);
    if (active && active.isPaused) {
        streamState.textContent = '⏸ Подключение к эфиру (на паузе)...';
    } else {
        streamState.textContent = 'Подключение к эфиру...';
    }

    const streamUrl = `/stream/${stationId}?t=${Date.now()}`;
    audio.src = streamUrl;
    audio.load();

    clearTimeout(tuneTimeout);
    tuneTimeout = setTimeout(() => {
        if (tuneId !== currentTuneId) return;
        const currentActive = stations.find(s => s.id === currentStationId);
        if (currentActive && currentActive.isPaused) {
            isPlaying = true;
            updateControlsUI();
            streamState.textContent = '⏸ Эфир на паузе';
        }
    }, 2500);

    const playPromise = audio.play();
    if (playPromise !== undefined) {
        playPromise
            .then(() => {
                if (tuneId !== currentTuneId) return; // Устаревший запрос переключения
                isPlaying = true;
                isPlaybackStoppedByUser = false;
                clearTimeout(tuneTimeout);
                updateControlsUI();
                updateNowPlayingUI();
            })
            .catch(error => {
                if (tuneId !== currentTuneId) return; // Устаревший запрос, игнорируем
                clearTimeout(tuneTimeout);
                const currentActive = stations.find(s => s.id === currentStationId);
                if (currentActive && currentActive.isPaused) {
                    isPlaying = true;
                    updateControlsUI();
                    streamState.textContent = '⏸ Эфир на паузе';
                } else {
                    console.warn('Воспроизведение заблокировано браузером или прервано:', error);
                    isPlaying = false;
                    updateControlsUI();
                    streamState.textContent = 'Нажмите Play для запуска вещания';
                }
            });
    }

    renderStations();
    updateNowPlayingUI();
}

// 6. Переключение воспроизведения (Play / Pause)
function togglePlayPause() {
    if (isPlaying) {
        stopAudio();
        isPlaying = false;
        isPlaybackStoppedByUser = true;
        frozenElapsedSeconds = Math.max(0, currentTrackDuration - currentTrackRemaining);
        streamState.textContent = 'Эфир остановлен';
        updateControlsUI();
        renderTrackProgress();
    } else {
        isPlaybackStoppedByUser = false;
        tune(currentStationId);
    }
}

// Привязываем обработчик клика на главную кнопку Play/Pause
if (btnPlayPause) {
    btnPlayPause.addEventListener('click', togglePlayPause);
}

// 7. Обновление текстовой информации о текущем эфире и времени трека
function updateNowPlayingUI() {
    const active = stations.find(s => s.id === currentStationId);
    if (active) {
        currentStationName.textContent = active.name;
        currentTrackTitle.textContent = active.currentTrack || 'В эфире';
        currentStationListeners.textContent = active.listeners;

        const trackChanged = active.currentTrack && active.currentTrack !== lastKnownTrack;
        if (trackChanged) {
            lastKnownTrack = active.currentTrack;
        }

        // Синхронизация времени трека от сервера
        if (typeof active.trackRemainingSeconds === 'number') {
            if (isPlaying) {
                currentTrackRemaining = Math.max(0, active.trackRemainingSeconds);
                currentTrackDuration = Math.max(0, active.trackDurationSeconds || 0);
                frozenElapsedSeconds = Math.max(0, currentTrackDuration - currentTrackRemaining);
                renderTrackProgress();
            } else if (isPlaybackStoppedByUser) {
                // Если воспроизведение остановлено пользователем, время и прогресс НЕ идут вперед!
                if (trackChanged) {
                    currentTrackDuration = Math.max(0, active.trackDurationSeconds || 0);
                    currentTrackRemaining = currentTrackDuration;
                    frozenElapsedSeconds = 0;
                }
                renderTrackProgress();
            } else {
                // Начальное состояние до первого запуска
                currentTrackRemaining = Math.max(0, active.trackRemainingSeconds);
                currentTrackDuration = Math.max(0, active.trackDurationSeconds || 0);
                frozenElapsedSeconds = Math.max(0, currentTrackDuration - currentTrackRemaining);
                renderTrackProgress();
            }
        }

        // Интеграция с MediaSession API браузера/ОС
        if ('mediaSession' in navigator) {
            navigator.mediaSession.metadata = new MediaMetadata({
                title: active.currentTrack || active.name,
                artist: active.name,
                album: 'Радио'
            });
            try {
                navigator.mediaSession.setActionHandler('play', () => {
                    if (!isPlaying) {
                        isPlaybackStoppedByUser = false;
                        tune(currentStationId);
                    }
                });
                navigator.mediaSession.setActionHandler('pause', () => {
                    if (isPlaying) togglePlayPause();
                });
                navigator.mediaSession.setActionHandler('stop', () => {
                    if (isPlaying) togglePlayPause();
                });
            } catch (e) { }
        }
    }
}

// Отрисовка прогресса и обратного отсчета трека
function renderTrackProgress() {
    if (!trackTimeElapsed || !trackTimeRemaining || !trackProgressFill) return;

    const active = stations.find(s => s.id === currentStationId);

    if (currentTrackDuration > 0) {
        const elapsed = (!isPlaying && isPlaybackStoppedByUser)
            ? frozenElapsedSeconds
            : Math.max(0, currentTrackDuration - currentTrackRemaining);

        trackTimeElapsed.textContent = formatTime(elapsed);

        if (!isPlaying && isPlaybackStoppedByUser) {
            trackTimeRemaining.textContent = `⏸ Остановлено (${formatTime(currentTrackRemaining)} осталось)`;
        } else if (active && active.isLiveDj) {
            trackTimeRemaining.textContent = '🎙️ Ведущий в эфире';
        } else if (active && active.isPaused) {
            trackTimeRemaining.textContent = `⏸ На паузе (${formatTime(currentTrackRemaining)})`;
        } else if (currentTrackRemaining > 0) {
            trackTimeRemaining.textContent = `⏳ осталось: ${formatTime(currentTrackRemaining)}`;
        } else {
            trackTimeRemaining.textContent = '⏳ Смена трека...';
        }

        const pct = Math.min(100, Math.max(0, (elapsed / currentTrackDuration) * 100));
        trackProgressFill.style.width = `${pct}%`;
    } else {
        trackTimeElapsed.textContent = '00:00';
        trackTimeRemaining.textContent = (!isPlaying && isPlaybackStoppedByUser)
            ? '⏸ Остановлено'
            : ((active && active.isPaused) ? '⏸ На паузе' : (active && active.isLiveDj ? '🎙️ Ведущий в эфире' : '⏳ Прямой эфир'));
        trackProgressFill.style.width = (!isPlaying && isPlaybackStoppedByUser) ? '0%' : '100%';
    }
}

// 8. Обновление визуального состояния кнопок и анимаций
function updateControlsUI() {
    const active = stations.find(s => s.id === currentStationId);
    if (isPlaying) {
        playIcon.textContent = '⏸';
        visualizer.classList.add('active');
        if (active && active.isLiveDj) {
            streamState.textContent = '🎙️ Ведущий в прямом эфире';
        } else if (active && active.isPaused) {
            streamState.textContent = '⏸ Эфир на паузе';
        } else {
            streamState.textContent = 'В прямом эфире';
        }
    } else {
        playIcon.textContent = '▶';
        visualizer.classList.remove('active');
    }
}

// 9. События аудио элемента
audio.addEventListener('playing', () => {
    isPlaying = true;
    clearTimeout(tuneTimeout);
    updateControlsUI();
    const active = stations.find(s => s.id === currentStationId);
    if (active && active.isPaused) {
        streamState.textContent = '⏸ Эфир на паузе';
    } else if (active && active.isLiveDj) {
        streamState.textContent = '🎙️ Ведущий в прямом эфире';
    } else {
        streamState.textContent = 'В прямом эфире';
    }
});

audio.addEventListener('pause', () => {
    if (audio.src && isPlaying) {
        isPlaying = false;
        isPlaybackStoppedByUser = true;
        frozenElapsedSeconds = Math.max(0, currentTrackDuration - currentTrackRemaining);
        updateControlsUI();
        renderTrackProgress();
        streamState.textContent = 'Воспроизведение приостановлено';
    }
});

audio.addEventListener('waiting', () => {
    const active = stations.find(s => s.id === currentStationId);
    if (active && active.isPaused) {
        streamState.textContent = '⏸ Эфир на паузе';
    } else if (isPlaying) {
        streamState.textContent = 'Буферизация потока...';
    }
});

audio.addEventListener('error', (e) => {
    if (isPlaybackStoppedByUser || !audio.src || audio.src === '' || audio.src === window.location.href || audio.networkState === HTMLMediaElement.NETWORK_EMPTY) {
        return;
    }

    const active = stations.find(s => s.id === currentStationId);
    if (active && active.isPaused) {
        // Если станция на паузе у сервера, стрим передает тишину — это нормальное состояние паузы
        console.warn('Станция находится на паузе у сервера');
        isPlaying = true;
        updateControlsUI();
        streamState.textContent = '⏸ Эфир на паузе';
        return;
    }

    console.error('Audio stream error:', e, audio.error);
    stopAudio();
    isPlaying = false;
    updateControlsUI();
    streamState.textContent = 'Ошибка воспроизведения потока';
});

// 10. Регулятор громкости и Mute
volumeSlider.addEventListener('input', (e) => {
    const val = parseFloat(e.target.value);
    audio.volume = val;
    volumeValue.textContent = Math.round(val * 100) + '%';
    btnMute.textContent = val === 0 ? '🔇' : '🔊';
});

btnMute.addEventListener('click', () => {
    if (audio.muted || audio.volume === 0) {
        audio.muted = false;
        audio.volume = 0.8;
        volumeSlider.value = 0.8;
        volumeValue.textContent = '80%';
        btnMute.textContent = '🔊';
    } else {
        audio.muted = true;
        volumeSlider.value = 0;
        volumeValue.textContent = '0%';
        btnMute.textContent = '🔇';
    }
});

// 11. Посекундный локальный отсчет оставшегося времени трека и синхронизация прямого эфира
setInterval(() => {
    const active = stations.find(s => s.id === currentStationId);

    // Контроль минимальной задержки прямого эфира (Live audio sync)
    if (isPlaying && !audio.paused && audio.buffered.length > 0) {
        try {
            const bufferedEnd = audio.buffered.end(audio.buffered.length - 1);
            const liveLatency = bufferedEnd - audio.currentTime;
            if (liveLatency > 4.0) {
                // Если буфер сильно отстал (например, после сворачивания вкладки), мгновенно подтягиваемся к живой точке
                audio.currentTime = bufferedEnd - 1.0;
            } else if (liveLatency > 2.2) {
                audio.playbackRate = 1.08;
            } else if (liveLatency <= 1.2) {
                audio.playbackRate = 1.0;
            }
        } catch (e) { }
    }

    // Не двигаем прогресс, если плеер выключен, аудио на паузе или эфир на паузе / Live DJ!
    if (!isPlaying || audio.paused || (active && (active.isPaused || active.isLiveDj))) {
        return;
    }

    if (currentTrackRemaining > 0) {
        currentTrackRemaining--;
        renderTrackProgress();
    }
}, 1000);

// 12. Периодическое обновление данных с сервера (каждые 3 секунды)
setInterval(fetchStations, 3000);

// Первоначальный старт: загружаем список и готовим интерфейс
fetchStations().then(() => {
    updateNowPlayingUI();
});
