(() => {
  'use strict';

  // 이 키에는 화면 시안의 설정 선택값만 저장한다. 게임 자동 저장과 분리한다.
  const SETTINGS_KEY = 'gamsamo.startMenuPreview.settings.v1';
  const DEFAULT_SETTINGS = Object.freeze({
    displayMode: 'windowed',
    resolution: '1920x1080',
    quality: 'medium',
    masterVolume: 80,
    bgmVolume: 65,
    sfxVolume: 70
  });
  const SELECT_OPTIONS = {
    displayMode: ['windowed', 'fullscreen'],
    resolution: ['1280x720', '1600x900', '1920x1080'],
    quality: ['low', 'medium', 'high']
  };
  const VOLUME_KEYS = ['masterVolume', 'bgmVolume', 'sfxVolume'];
  const byId = id => document.getElementById(id);
  const titleScreen = byId('titleScreen');
  const exitScreen = byId('exitScreen');
  const settingsDialog = byId('settingsDialog');
  const settingsForm = byId('settingsForm');
  const settingsError = byId('settingsError');
  const toast = byId('statusToast');
  const menuButtons = [...document.querySelectorAll('.menu-button:not(:disabled)')];
  let appliedSettings = loadPreviewSettings();
  let lastModalTrigger = null;
  let toastTimer = null;

  function sanitizeSettings(candidate) {
    const result = { ...DEFAULT_SETTINGS };
    if (!candidate || typeof candidate !== 'object') return result;
    for (const [key, options] of Object.entries(SELECT_OPTIONS)) {
      if (options.includes(candidate[key])) result[key] = candidate[key];
    }
    for (const key of VOLUME_KEYS) {
      if (typeof candidate[key] === 'number' && Number.isFinite(candidate[key])) {
        result[key] = Math.min(100, Math.max(0, Math.round(candidate[key])));
      }
    }
    return result;
  }

  function loadPreviewSettings() {
    try { return sanitizeSettings(JSON.parse(localStorage.getItem(SETTINGS_KEY))); }
    catch (_) { return { ...DEFAULT_SETTINGS }; }
  }

  function updateVolumeDisplay(input) {
    const value = Number(input.value);
    byId(`${input.id}Value`).value = `${value}%`;
    input.style.setProperty('--range-value', `${value}%`);
    input.setAttribute('aria-valuetext', `${value}%`);
  }

  function populateSettings(settings) {
    for (const [key, value] of Object.entries(settings)) byId(key).value = value;
    VOLUME_KEYS.forEach(key => updateVolumeDisplay(byId(key)));
    settingsError.hidden = true;
  }

  function readSettingsForm() {
    const draft = {};
    Object.keys(SELECT_OPTIONS).forEach(key => { draft[key] = byId(key).value; });
    VOLUME_KEYS.forEach(key => { draft[key] = Number(byId(key).value); });
    return sanitizeSettings(draft);
  }

  function selectMenuButton(button) {
    menuButtons.forEach(item => item.classList.toggle('is-selected', item === button));
  }

  function openModal(dialog, trigger) {
    if (document.querySelector('dialog[open]')) return;
    lastModalTrigger = trigger;
    toast.classList.remove('is-visible');
    dialog.showModal();
  }

  function closeModal(dialog) {
    if (!dialog.open) return;
    dialog.close();
    if (dialog === settingsDialog) populateSettings(appliedSettings);
    if (lastModalTrigger?.isConnected) lastModalTrigger.focus({ preventScroll: true });
  }

  function trapModalFocus(event, dialog) {
    if (event.key !== 'Tab') return;
    const focusable = [...dialog.querySelectorAll('button:not(:disabled), select:not(:disabled), input:not(:disabled), [tabindex="0"]')]
      .filter(element => element.getClientRects().length > 0);
    if (!focusable.length) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && (document.activeElement === first || !focusable.includes(document.activeElement))) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && (document.activeElement === last || !focusable.includes(document.activeElement))) {
      event.preventDefault();
      first.focus();
    }
  }

  function showToast(message) {
    clearTimeout(toastTimer);
    toast.textContent = message;
    toast.classList.add('is-visible');
    toastTimer = setTimeout(() => toast.classList.remove('is-visible'), 2200);
  }

  menuButtons.forEach(button => {
    button.addEventListener('pointerenter', () => selectMenuButton(button));
    button.addEventListener('focus', () => selectMenuButton(button));
    button.addEventListener('keydown', event => {
      if (!['ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const current = menuButtons.indexOf(button);
      const target = event.key === 'Home' ? 0 : event.key === 'End' ? menuButtons.length - 1 :
        (current + (event.key === 'ArrowDown' ? 1 : -1) + menuButtons.length) % menuButtons.length;
      menuButtons[target].focus();
    });
  });

  document.querySelectorAll('dialog').forEach(dialog => {
    dialog.addEventListener('keydown', event => trapModalFocus(event, dialog));
    dialog.addEventListener('cancel', event => {
      event.preventDefault();
      closeModal(dialog);
    });
    dialog.addEventListener('click', event => {
      if (event.target !== dialog) return;
      const bounds = dialog.getBoundingClientRect();
      const outside = event.clientX < bounds.left || event.clientX > bounds.right ||
        event.clientY < bounds.top || event.clientY > bounds.bottom;
      if (outside) closeModal(dialog);
    });
  });

  byId('settingsButton').addEventListener('click', event => {
    populateSettings(appliedSettings);
    openModal(settingsDialog, event.currentTarget);
  });
  byId('defaultsButton').addEventListener('click', () => populateSettings(DEFAULT_SETTINGS));
  byId('cancelSettingsButton').addEventListener('click', () => closeModal(settingsDialog));
  VOLUME_KEYS.forEach(key => byId(key).addEventListener('input', event => updateVolumeDisplay(event.currentTarget)));
  settingsForm.addEventListener('submit', event => {
    event.preventDefault();
    const draft = readSettingsForm();
    try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(draft)); }
    catch (_) { settingsError.hidden = false; return; }
    appliedSettings = draft;
    closeModal(settingsDialog);
    showToast('설정을 적용했습니다');
  });

  byId('newGameButton').addEventListener('click', event => openModal(byId('newGameDialog'), event.currentTarget));
  byId('cancelNewGameButton').addEventListener('click', () => closeModal(byId('newGameDialog')));
  byId('confirmNewGameButton').addEventListener('click', () => closeModal(byId('newGameDialog')));
  byId('quitButton').addEventListener('click', event => openModal(byId('quitDialog'), event.currentTarget));
  byId('cancelQuitButton').addEventListener('click', () => closeModal(byId('quitDialog')));
  byId('confirmQuitButton').addEventListener('click', () => {
    closeModal(byId('quitDialog'));
    titleScreen.hidden = true;
    exitScreen.hidden = false;
    byId('returnToTitleButton').focus({ preventScroll: true });
  });
  byId('returnToTitleButton').addEventListener('click', () => {
    exitScreen.hidden = true;
    titleScreen.hidden = false;
    byId('newGameButton').focus({ preventScroll: true });
  });

  populateSettings(appliedSettings);
})();
