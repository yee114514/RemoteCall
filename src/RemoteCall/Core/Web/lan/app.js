(function () {
  'use strict';

  var DEVICE_ID_KEY = 'remotecall.deviceId';
  var DEVICE_NAME_KEY = 'remotecall.deviceName';
  // 服务端把空名字记成这个占位名；把它当成"还没设置过名字"
  var PLACEHOLDER_NAME = '未命名设备';

  var listEl = document.getElementById('list');
  var emptyEl = document.getElementById('empty');
  var composer = document.getElementById('composer');
  var textArea = document.getElementById('text');
  var sendButton = document.getElementById('send');
  var deviceNameEl = document.getElementById('device-name');
  var renameButton = document.getElementById('rename-btn');
  var logoutButton = document.getElementById('logout-btn');
  var tabsEl = document.getElementById('tabs');
  var toastEl = document.getElementById('toast');

  var state = {
    filter: 'all',
    version: -1,
    total: -1,
    deviceId: '',
    deviceName: '',
    polling: false,
    booted: false,
    toastTimer: 0
  };

  // ---------- 基础工具 ----------

  function newDeviceId() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
      return window.crypto.randomUUID();
    }
    var bytes = new Uint8Array(16);
    window.crypto.getRandomValues(bytes);
    var parts = [];
    for (var i = 0; i < bytes.length; i++) {
      parts.push((bytes[i] + 0x100).toString(16).slice(1));
    }
    var hex = parts.join('');
    return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16) + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
  }

  function ensureDeviceId() {
    var id = '';
    try { id = localStorage.getItem(DEVICE_ID_KEY) || ''; } catch (e) { }
    if (!id) {
      id = newDeviceId();
      try { localStorage.setItem(DEVICE_ID_KEY, id); } catch (e) { }
    }
    return id;
  }

  function readStoredName() {
    try { return localStorage.getItem(DEVICE_NAME_KEY) || ''; } catch (e) { return ''; }
  }

  function storeName(name) {
    try { localStorage.setItem(DEVICE_NAME_KEY, name); } catch (e) { }
  }

  function toast(message) {
    toastEl.textContent = message;
    toastEl.hidden = false;
    if (state.toastTimer) { clearTimeout(state.toastTimer); }
    state.toastTimer = setTimeout(function () { toastEl.hidden = true; }, 2600);
  }

  function formatTime(iso) {
    var date = new Date(iso);
    if (isNaN(date.getTime())) { return ''; }
    var now = new Date();
    var sameDay = date.toDateString() === now.toDateString();
    var time = ('0' + date.getHours()).slice(-2) + ':' + ('0' + date.getMinutes()).slice(-2);
    if (sameDay) { return time; }
    return (date.getMonth() + 1) + '月' + date.getDate() + '日 ' + time;
  }

  function api(path, options) {
    var init = options || {};
    init.headers = init.headers || {};
    return fetch(path, init).then(function (response) {
      if (response.status === 401) {
        location.replace('/login');
        throw new Error('unauthorized');
      }
      return response.json().catch(function () { return {}; }).then(function (data) {
        if (!response.ok || data.ok === false) {
          throw new Error((data && data.message) || ('请求失败（' + response.status + '）'));
        }
        return data;
      });
    });
  }

  // ---------- 渲染 ----------

  function render(messages) {
    listEl.textContent = '';
    if (!messages || messages.length === 0) {
      listEl.hidden = true;
      emptyEl.hidden = false;
      return;
    }

    listEl.hidden = false;
    emptyEl.hidden = true;
    var fragment = document.createDocumentFragment();
    for (var i = 0; i < messages.length; i++) {
      fragment.appendChild(buildCard(messages[i]));
    }
    listEl.appendChild(fragment);
  }

  function buildCard(message) {
    var card = document.createElement('article');
    card.className = 'card' + (message.completed ? ' done' : '');

    var text = document.createElement('div');
    text.className = 'card-text';
    text.textContent = message.text;

    var meta = document.createElement('div');
    meta.className = 'card-meta';
    var badge = document.createElement('span');
    badge.className = 'badge' + (message.completed ? ' ok' : '');
    badge.textContent = message.completed ? '已完成展示' : '待展示';
    meta.appendChild(badge);
    meta.appendChild(document.createTextNode('·'));
    meta.appendChild(document.createTextNode(message.deviceName || '未知设备'));
    meta.appendChild(document.createTextNode('·'));
    meta.appendChild(document.createTextNode(message.ip || ''));
    meta.appendChild(document.createTextNode('·'));
    meta.appendChild(document.createTextNode(formatTime(message.createdAt)));

    var actions = document.createElement('div');
    actions.className = 'card-actions';
    var toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.textContent = message.completed ? '撤销完成' : '标记完成';
    toggle.addEventListener('click', function () { toggleComplete(message, toggle); });
    actions.appendChild(toggle);

    card.appendChild(text);
    card.appendChild(meta);
    card.appendChild(actions);
    return card;
  }

  // ---------- 交互 ----------

  function toggleComplete(message, button) {
    button.disabled = true;
    api('/api/messages/' + encodeURIComponent(message.id) + '/complete', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ completed: !message.completed })
    }).then(function () {
      toast(message.completed ? '已撤销完成标记' : '已标记为完成展示');
      return poll(true);
    }).catch(function (error) {
      toast(error.message);
    }).then(function () {
      button.disabled = false;
    });
  }

  function renameOnServer(name, notify) {
    return api('/api/devices/' + encodeURIComponent(state.deviceId), {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: name, requesterId: state.deviceId })
    }).then(function (data) {
      state.deviceName = (data.device && data.device.name) || name;
      storeName(state.deviceName);
      deviceNameEl.textContent = state.deviceName;
      if (notify) {
        toast('设备名已改为「' + state.deviceName + '」');
        return poll(true);
      }
      return null;
    });
  }

  function askRename() {
    var input = window.prompt('修改设备名（历史消息里的名字会一起更新）', state.deviceName || '');
    if (input === null) { return; }
    var name = input.trim();
    if (!name) {
      toast('设备名不能为空');
      return;
    }
    renameOnServer(name, true).catch(function (error) { toast(error.message); });
  }

  function poll(force) {
    if (state.polling) { return Promise.resolve(); }
    state.polling = true;
    return api('/api/messages?filter=' + encodeURIComponent(state.filter) + '&limit=200')
      .then(function (data) {
        state.polling = false;
        var totalChanged = state.total >= 0 && typeof data.total === 'number' && data.total !== state.total;
        if (typeof data.total === 'number') { state.total = data.total; }
        if (!force && state.version === data.version && !totalChanged) { return; }
        state.version = data.version;
        render(data.messages || []);
        if (totalChanged && state.booted) { toast('收到了新消息'); }
      })
      .catch(function (error) {
        state.polling = false;
        if (error.message !== 'unauthorized') { console.warn(error.message); }
      });
  }

  // ---------- 设备名探测 ----------
  // 浏览器不提供主机名，只能按 UA 识别设备类型 / Android 机型
  function detectDeviceName() {
    var ua = navigator.userAgent || '';
    var touchPoints = navigator.maxTouchPoints || 0;

    if (/iPhone/.test(ua)) { return 'iPhone'; }
    if (/iPad/.test(ua) || (/Macintosh/.test(ua) && touchPoints > 1)) { return 'iPad'; }
    if (/iPod/.test(ua)) { return 'iPod touch'; }
    if (/Android/i.test(ua)) { return extractAndroidModel(ua) || 'Android 设备'; }
    if (/CrOS/.test(ua)) { return 'Chromebook'; }
    if (/Windows/i.test(ua)) { return 'Windows 电脑'; }
    if (/Macintosh|Mac OS X/i.test(ua)) { return 'Mac'; }
    if (/Linux/i.test(ua)) { return 'Linux 设备'; }
    return '浏览器设备';
  }

  function extractAndroidModel(ua) {
    var index = ua.indexOf('Android');
    if (index < 0) { return ''; }
    var rest = ua.slice(index + 'Android'.length);
    var semicolon = rest.indexOf(';');
    if (semicolon < 0) { return ''; }

    var candidate = rest.slice(semicolon + 1);
    var build = candidate.search(/Build\//i);
    if (build >= 0) {
      candidate = candidate.slice(0, build);
    } else {
      var close = candidate.indexOf(')');
      if (close >= 0) { candidate = candidate.slice(0, close); }
    }

    candidate = candidate.replace(/;\s*$/, '').trim();
    if (candidate.length < 2 || candidate.length > 40) { return ''; }
    if (/^Android/i.test(candidate) || /^wv$/i.test(candidate) || /^K$/i.test(candidate)) { return ''; }
    return candidate;
  }

  // ---------- 启动 ----------

  function loadSession() {
    state.deviceId = ensureDeviceId();
    var storedName = readStoredName();

    return api('/api/session?deviceId=' + encodeURIComponent(state.deviceId)).then(function (data) {
      var serverName = data.deviceName || '';
      var name;

      if (serverName && serverName !== PLACEHOLDER_NAME) {
        // 服务器上已经有有效名字（可能是之前改过的），以它为准
        name = serverName;
      } else if (storedName && storedName !== PLACEHOLDER_NAME) {
        name = storedName;
      } else {
        // 首次使用：按 UA 探测设备名，而不是「未命名设备」
        name = detectDeviceName();
      }

      state.deviceName = name;
      storeName(name);
      deviceNameEl.textContent = name;

      if (name !== serverName) {
        return renameOnServer(name, false).catch(function () { });
      }
      return null;
    });
  }

  function boot() {
    if (!window.fetch || !window.Promise) {
      document.body.textContent = '浏览器版本过低，请使用较新的浏览器访问。';
      return;
    }

    composer.addEventListener('submit', function (event) {
      event.preventDefault();
      var text = textArea.value.trim();
      if (!text) {
        toast('请输入消息内容');
        return;
      }
      sendButton.disabled = true;
      api('/api/messages', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: text, deviceId: state.deviceId, deviceName: state.deviceName })
      }).then(function () {
        textArea.value = '';
        toast('已发送到本机');
        return poll(true);
      }).catch(function (error) {
        toast(error.message);
      }).then(function () {
        sendButton.disabled = false;
      });
    });

    tabsEl.addEventListener('click', function (event) {
      var button = event.target.closest('.tab');
      if (!button) { return; }
      var siblings = tabsEl.querySelectorAll('.tab');
      for (var i = 0; i < siblings.length; i++) {
        siblings[i].classList.toggle('active', siblings[i] === button);
      }
      state.filter = button.getAttribute('data-filter') || 'all';
      state.version = -1;
      poll(true);
    });

    renameButton.addEventListener('click', askRename);

    logoutButton.addEventListener('click', function () {
      api('/api/logout', { method: 'POST' }).then(function () {
        location.replace('/login');
      }).catch(function () {
        location.replace('/login');
      });
    });

    document.addEventListener('visibilitychange', function () {
      if (!document.hidden) { poll(false); }
    });

    loadSession().then(function () {
      state.booted = true;
      return poll(true);
    }).catch(function (error) {
      if (error.message !== 'unauthorized') { toast(error.message); }
    });

    setInterval(function () { if (!document.hidden) { poll(false); } }, 2000);
  }

  boot();
})();
