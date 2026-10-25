(function () {
  'use strict';

  var state = { data: null, filter: 'all', timer: 0, busy: false };
  var toastEl = document.getElementById('toast');

  var els = {
    statusGrid: document.getElementById('status-grid'),
    passwordHint: document.getElementById('password-hint'),
    newPassword: document.getElementById('new-password'),
    setPassword: document.getElementById('set-password-btn'),
    genPassword: document.getElementById('generate-password-btn'),
    lanEnabled: document.getElementById('lan-enabled'),
    lanPort: document.getElementById('lan-port'),
    adminPort: document.getElementById('admin-port'),
    sessionDays: document.getElementById('session-days'),
    retention: document.getElementById('retention'),
    notifEnabled: document.getElementById('notif-enabled'),
    notifMask: document.getElementById('notif-mask'),
    notifOverlay: document.getElementById('notif-overlay'),
    notifSpeech: document.getElementById('notif-speech'),
    saveSettings: document.getElementById('save-settings-btn'),
    settingsHint: document.getElementById('settings-hint'),
    messageFilter: document.getElementById('message-filter'),
    messageRows: document.getElementById('message-rows'),
    deviceRows: document.getElementById('device-rows'),
    logList: document.getElementById('log-list'),
    clearDone: document.getElementById('clear-done-btn'),
    clearAll: document.getElementById('clear-all-btn'),
    refresh: document.getElementById('refresh-btn'),
    restart: document.getElementById('restart-btn'),
    selftest: document.getElementById('selftest-btn')
  };

  function toast(message) {
    toastEl.textContent = message;
    toastEl.hidden = false;
    clearTimeout(state.timer);
    state.timer = setTimeout(function () { toastEl.hidden = true; }, 3000);
  }

  function formatTime(iso) {
    if (!iso) { return '—'; }
    var date = new Date(iso);
    if (isNaN(date.getTime())) { return '—'; }
    var pad = function (n) { return ('0' + n).slice(-2); };
    return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate()) + ' ' +
      pad(date.getHours()) + ':' + pad(date.getMinutes()) + ':' + pad(date.getSeconds());
  }

  function api(path, options) {
    var init = options || {};
    init.headers = init.headers || {};
    return fetch(path, init).then(function (response) {
      return response.json().catch(function () { return {}; }).then(function (data) {
        if (!response.ok || data.ok === false) {
          throw new Error((data && data.message) || ('请求失败（' + response.status + '）'));
        }
        return data;
      });
    });
  }

  function postJson(path, payload) {
    return api(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload || {})
    });
  }

  function putJson(path, payload) {
    return api(path, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload || {})
    });
  }

  // ---------- 状态 ----------

  function statusItem(label, value, className) {
    var box = document.createElement('div');
    box.className = 'status-item';
    var labelEl = document.createElement('div');
    labelEl.className = 'label';
    labelEl.textContent = label;
    var valueEl = document.createElement('div');
    valueEl.className = 'value ' + (className || '');
    valueEl.textContent = value;
    box.appendChild(labelEl);
    box.appendChild(valueEl);
    return box;
  }

  function renderStatus(data) {
    var status = data.status || {};
    var settings = data.settings || {};
    els.statusGrid.textContent = '';

    var lanValue;
    var lanClass = 'ok';
    if (!status.lanEnabled) {
      lanValue = '已关闭';
      lanClass = 'warn';
    } else if (status.lanRunning) {
      lanValue = '运行中（端口 ' + status.lanPort + '）';
    } else {
      lanValue = status.lanError || '未运行';
      lanClass = 'bad';
    }

    els.statusGrid.appendChild(statusItem('局域网网页', lanValue, lanClass));

    var addresses = (status.lanUrls && status.lanUrls.length)
      ? status.lanUrls.join('\n')
      : (status.lanRunning ? '未找到局域网地址' : '—');
    els.statusGrid.appendChild(statusItem('局域网访问地址', addresses));

    var adminValue = status.adminRunning ? ('运行中（端口 ' + status.adminPort + '）') : (status.adminError || '未运行');
    els.statusGrid.appendChild(statusItem('本机管理页', adminValue, status.adminRunning ? 'ok' : 'bad'));
    els.statusGrid.appendChild(statusItem('已处理请求数', String(status.requestCount || 0)));
    els.statusGrid.appendChild(statusItem('活动连接数', String(status.activeConnections || 0)));
    els.statusGrid.appendChild(statusItem('消息 / 未完成', (data.messages ? data.messages.length : 0) + ' / ' +
      (data.messages ? data.messages.filter(function (m) { return !m.completed; }).length : 0)));
    els.statusGrid.appendChild(statusItem('服务启动时间', formatTime(status.startedAt)));

    els.passwordHint.textContent = settings.generatedPassword
      ? ('当前使用默认密码：「' + settings.generatedPassword + '」，建议改成你自己的密码。')
      : '已设置自定义访问密码。';
  }

  function renderSettings(data) {
    var settings = data.settings || {};
    if (document.activeElement !== els.lanPort) { els.lanPort.value = settings.lanPort; }
    if (document.activeElement !== els.adminPort) { els.adminPort.value = settings.adminPort; }
    if (document.activeElement !== els.sessionDays) { els.sessionDays.value = settings.sessionDays; }
    if (document.activeElement !== els.retention) { els.retention.value = settings.messageRetention; }
    if (document.activeElement !== els.notifMask) { els.notifMask.value = settings.notificationMaskSeconds; }
    if (document.activeElement !== els.notifOverlay) { els.notifOverlay.value = settings.notificationOverlaySeconds; }
    els.lanEnabled.checked = !!settings.lanEnabled;
    els.notifEnabled.checked = !!settings.notificationEnabled;
    els.notifSpeech.checked = !!settings.notificationSpeech;
  }

  // ---------- 表格 ----------

  function emptyRow(tbody, columns, text) {
    var tr = document.createElement('tr');
    tr.className = 'empty-row';
    var td = document.createElement('td');
    td.colSpan = columns;
    td.textContent = text;
    tr.appendChild(td);
    tbody.appendChild(tr);
  }

  function renderMessages(data) {
    var messages = (data.messages || []).filter(function (m) {
      if (state.filter === 'pending') { return !m.completed; }
      if (state.filter === 'done') { return m.completed; }
      return true;
    });

    els.messageRows.textContent = '';
    if (messages.length === 0) {
      emptyRow(els.messageRows, 7, '暂无消息');
      return;
    }

    messages.forEach(function (message) {
      var tr = document.createElement('tr');
      if (message.completed) { tr.className = 'done'; }

      var idTd = document.createElement('td');
      idTd.textContent = message.id;
      tr.appendChild(idTd);

      var timeTd = document.createElement('td');
      timeTd.textContent = formatTime(message.createdAt);
      tr.appendChild(timeTd);

      var deviceTd = document.createElement('td');
      deviceTd.textContent = message.deviceName || '未知设备';
      tr.appendChild(deviceTd);

      var ipTd = document.createElement('td');
      ipTd.textContent = message.ip || '';
      tr.appendChild(ipTd);

      var textTd = document.createElement('td');
      textTd.className = 'text';
      textTd.textContent = message.text;
      tr.appendChild(textTd);

      var statusTd = document.createElement('td');
      var badge = document.createElement('span');
      badge.className = 'badge' + (message.completed ? ' ok' : '');
      badge.textContent = message.completed ? '已完成' : '未完成';
      statusTd.appendChild(badge);
      tr.appendChild(statusTd);

      var actionsTd = document.createElement('td');
      actionsTd.className = 'actions';
      var toggle = document.createElement('button');
      toggle.type = 'button';
      toggle.textContent = message.completed ? '撤销完成' : '标记完成';
      toggle.addEventListener('click', function () {
        toggle.disabled = true;
        postJson('/api/messages/' + message.id + '/complete', { completed: !message.completed })
          .then(refresh).catch(function (error) { toast(error.message); });
      });
      actionsTd.appendChild(toggle);

      var remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'danger';
      remove.textContent = '删除';
      remove.addEventListener('click', function () {
        if (!window.confirm('确定删除这条消息吗？')) { return; }
        remove.disabled = true;
        api('/api/messages/' + message.id + '/delete', { method: 'POST' })
          .then(refresh).catch(function (error) { toast(error.message); });
      });
      actionsTd.appendChild(remove);
      tr.appendChild(actionsTd);

      els.messageRows.appendChild(tr);
    });
  }

  function renderDevices(data) {
    var devices = data.devices || [];
    els.deviceRows.textContent = '';
    if (devices.length === 0) {
      emptyRow(els.deviceRows, 5, '还没有设备发过消息');
      return;
    }

    devices.forEach(function (device) {
      var tr = document.createElement('tr');

      var nameTd = document.createElement('td');
      nameTd.textContent = device.name;
      tr.appendChild(nameTd);

      var idTd = document.createElement('td');
      idTd.textContent = device.id;
      tr.appendChild(idTd);

      var ipTd = document.createElement('td');
      ipTd.textContent = device.lastIp || '';
      tr.appendChild(ipTd);

      var seenTd = document.createElement('td');
      seenTd.textContent = formatTime(device.lastSeenAt);
      tr.appendChild(seenTd);

      var actionsTd = document.createElement('td');
      actionsTd.className = 'actions';
      var rename = document.createElement('button');
      rename.type = 'button';
      rename.textContent = '改名';
      rename.addEventListener('click', function () {
        var input = window.prompt('修改设备名（该设备的历史消息会一起更新）', device.name);
        if (input === null) { return; }
        var name = input.trim();
        if (!name) { toast('设备名不能为空'); return; }
        putJson('/api/devices/' + encodeURIComponent(device.id), { name: name })
          .then(function () { toast('已改名'); return refresh(); })
          .catch(function (error) { toast(error.message); });
      });
      actionsTd.appendChild(rename);
      tr.appendChild(actionsTd);

      els.deviceRows.appendChild(tr);
    });
  }

  function renderLogs(data) {
    var logs = data.logs || [];
    els.logList.textContent = logs.length === 0
      ? '暂无日志'
      : logs.map(function (entry) {
        return formatTime(entry.time).slice(11) + '  ' + entry.message;
      }).join('\n');
  }

  // ---------- 刷新 ----------

  function refresh() {
    return api('/api/state?t=' + Date.now()).then(function (data) {
      state.data = data;
      renderStatus(data);
      renderSettings(data);
      renderMessages(data);
      renderDevices(data);
      renderLogs(data);
    }).catch(function (error) {
      toast(error.message);
    });
  }

  function collectSettings() {
    return {
      lanEnabled: els.lanEnabled.checked,
      lanPort: Number(els.lanPort.value),
      adminPort: Number(els.adminPort.value),
      sessionDays: Number(els.sessionDays.value),
      retention: Number(els.retention.value),
      notificationEnabled: els.notifEnabled.checked,
      notificationMaskSeconds: Number(els.notifMask.value),
      notificationOverlaySeconds: Number(els.notifOverlay.value),
      notificationSpeech: els.notifSpeech.checked
    };
  }

  function bind() {
    els.refresh.addEventListener('click', function () { refresh(); });

    els.restart.addEventListener('click', function () {
      els.restart.disabled = true;
      postJson('/api/servers/restart', {})
        .then(function () { toast('正在重启监听…'); setTimeout(refresh, 800); })
        .catch(function (error) { toast(error.message); })
        .then(function () { els.restart.disabled = false; });
    });

    els.selftest.addEventListener('click', function () {
      els.selftest.disabled = true;
      postJson('/api/selftest', {})
        .then(function () { toast('已插入测试消息'); return refresh(); })
        .catch(function (error) { toast(error.message); })
        .then(function () { els.selftest.disabled = false; });
    });

    els.setPassword.addEventListener('click', function () {
      var password = els.newPassword.value.trim();
      if (password.length < 4) { toast('密码至少 4 个字符'); return; }
      els.setPassword.disabled = true;
      postJson('/api/password', { password: password })
        .then(function () {
          els.newPassword.value = '';
          toast('密码已更新');
          return refresh();
        })
        .catch(function (error) { toast(error.message); })
        .then(function () { els.setPassword.disabled = false; });
    });

    els.genPassword.addEventListener('click', function () {
      els.genPassword.disabled = true;
      postJson('/api/password/generate', {})
        .then(function (data) {
          els.newPassword.value = data.password || '';
          toast('已生成新密码：' + (data.password || ''));
          return refresh();
        })
        .catch(function (error) { toast(error.message); })
        .then(function () { els.genPassword.disabled = false; });
    });

    els.saveSettings.addEventListener('click', function () {
      els.saveSettings.disabled = true;
      els.settingsHint.textContent = '保存中…';
      postJson('/api/settings', collectSettings())
        .then(function (data) {
          var status = data.status || {};
          els.settingsHint.textContent = '已保存 ' + formatTime(new Date().toISOString()).slice(11);
          if (status.adminPort && Number(els.adminPort.value) !== status.adminPort) {
            toast('端口已变更，请用新端口重新打开本页');
          } else {
            toast('设置已保存');
          }
          return refresh();
        })
        .catch(function (error) {
          els.settingsHint.textContent = '';
          toast(error.message);
        })
        .then(function () { els.saveSettings.disabled = false; });
    });

    els.messageFilter.addEventListener('change', function () {
      state.filter = els.messageFilter.value;
      if (state.data) { renderMessages(state.data); }
    });

    els.clearDone.addEventListener('click', function () {
      if (!window.confirm('确定清空所有已完成的消息吗？')) { return; }
      postJson('/api/messages/clear', { scope: 'done' })
        .then(function (data) { toast('已清空 ' + (data.removed || 0) + ' 条'); return refresh(); })
        .catch(function (error) { toast(error.message); });
    });

    els.clearAll.addEventListener('click', function () {
      if (!window.confirm('确定清空全部历史消息吗？此操作不可恢复。')) { return; }
      postJson('/api/messages/clear', { scope: 'all' })
        .then(function (data) { toast('已清空 ' + (data.removed || 0) + ' 条'); return refresh(); })
        .catch(function (error) { toast(error.message); });
    });

    document.addEventListener('keydown', function (event) {
      if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) { refresh(); }
    });
  }

  bind();
  refresh();
  setInterval(function () {
    var active = document.activeElement;
    var typing = active && (active.tagName === 'INPUT' || active.tagName === 'SELECT');
    if (!typing) { refresh(); }
  }, 5000);
})();
