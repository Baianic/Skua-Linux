(() => {
  'use strict';

  const state = {
    api: null,
    hotKeys: {},
    notifyTimer: null,
    logsTimer: null,
    packetLoggerTimer: null,
    interceptorTimer: null,
    loaderTimer: null,
    statsTimer: null,
    currentDropsTimer: null,
    selectedTravelIndex: -1,
    selectedPlugin: null,
    selectedSkill: null,
    interceptorCursor: 0,
    autoMode: 'attack'
  };

  const $ = (id) => document.getElementById(id);
  const qsa = (selector, root = document) => Array.from(root.querySelectorAll(selector));
  const esc = (value) => String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');

  function isSupportRepositoryScript(script) {
    const name = String(script?.name ?? script?.fileName ?? '').trim().toLowerCase();
    const path = String(script?.filePath ?? '').trim().toLowerCase();
    if (!name && !path) return false;
    if (/^core(?:\b|[ _.-])/.test(name)) return true;
    const fileName = path.split('/').pop() || '';
    if (/^core(?:\b|[ _.-]).*\.cs$/.test(fileName)) return true;
    return false;
  }

  function installStyles() {
    const style = document.createElement('style');
    style.id = 'skua-parity-v3-style';
    style.textContent = `
      .skua-menu-group { position: relative; display: flex; align-items: stretch; }
      .skua-menu-group > .skua-menu-button::after { content: ' ▾'; opacity: .75; }
      .skua-menu-dropdown {
        position: absolute; top: calc(100% - 1px); left: 0; min-width: 178px;
        display: none; padding: 4px; z-index: 10000;
        border: 1px solid #71638b; background: #2b2731;
        box-shadow: 0 8px 18px rgba(0,0,0,.45);
      }
      .skua-menu-group.open > .skua-menu-dropdown { display: grid; }
      .skua-menu-item {
        appearance: none; border: 0; background: transparent; color: #eee9f6;
        text-align: left; padding: 7px 10px; font: inherit; font-size: 13px;
        cursor: pointer; white-space: nowrap;
      }
      .skua-menu-item:hover { background: #604f78; }
      .skua-window.parity-wide { width: min(860px, calc(100vw - 24px)); }
      .skua-window.parity-medium { width: min(640px, calc(100vw - 24px)); }
      .skua-window.parity-small { width: min(430px, calc(100vw - 24px)); }
      .skua-window.parity-wide .skua-window-body,
      .skua-window.parity-medium .skua-window-body,
      .skua-window.parity-small .skua-window-body {
        max-height: calc(100vh - 112px);
        overflow: auto;
        scrollbar-gutter: stable;
      }
      #window-skills {
        width: min(920px, calc(100vw - 24px));
        height: min(760px, calc(100vh - 44px));
        left: max(12px, calc(50% - 460px));
        top: 12px;
      }
      #window-skills .skua-window-body { max-height: none; height: 100%; }
      .skills-editor-grid { display:grid; grid-template-columns:minmax(260px,1fr) minmax(300px,1fr); gap:8px; }
      .skills-editor-bottom { display:grid; grid-template-columns:2fr 1fr auto; gap:7px; align-items:end; margin-top:8px; }
      .parity-expander { padding:0; overflow:hidden; }
      .parity-expander > summary {
        cursor:pointer; list-style:none; padding:8px 10px; color:#eadff5;
        font-weight:700; background:#302b35; border-bottom:1px solid #5b5367;
      }
      .parity-expander > summary::-webkit-details-marker { display:none; }
      .parity-expander > summary::before { content:'▸'; display:inline-block; margin-right:7px; transition:transform .12s ease; }
      .parity-expander[open] > summary::before { transform:rotate(90deg); }
      .parity-expander-content { padding:8px; }
      .parity-toolbar { display:flex; flex-wrap:wrap; gap:5px; margin-bottom:7px; align-items:center; }
      .parity-grid { display:grid; grid-template-columns: repeat(2, minmax(0,1fr)); gap:7px; }
      .parity-grid.three { grid-template-columns: repeat(3, minmax(0,1fr)); }
      .parity-stack { display:grid; gap:6px; }
      .parity-card { border:1px solid #5b5367; background:#26232b; padding:7px; }
      .parity-section-title { color:#d9c9ef; font-weight:700; font-size:14px; margin:4px 0 6px; }
      .parity-option-row { display:grid; grid-template-columns:minmax(150px,1fr) minmax(120px,1fr); gap:8px; align-items:center; padding:5px 0; border-bottom:1px solid rgba(255,255,255,.05); }
      .parity-option-row:last-child { border-bottom:0; }
      .parity-option-label { color:#eee9f6; font-size:12.5px; }
      .parity-option-desc { color:#a9a3b1; font-size:10.5px; margin-top:2px; line-height:1.25; }
      .parity-list { border:1px solid #524b5c; min-height:110px; max-height:300px; overflow:auto; background:#1d1b20; }
      .parity-list-row { display:grid; grid-template-columns:auto minmax(0,1fr) auto; gap:7px; padding:5px 7px; border-bottom:1px solid rgba(255,255,255,.045); align-items:center; }
      .parity-list-row.selected { background:#514166; }
      .parity-list-row:hover { background:#39313f; }
      .parity-table { width:100%; border-collapse:collapse; font-size:11.5px; }
      .parity-table th,.parity-table td { border:1px solid #4e4856; padding:4px 6px; text-align:left; vertical-align:top; }
      .parity-table th { background:#302b35; color:#d9c9ef; position:sticky; top:0; }
      .parity-tabs { display:flex; gap:2px; margin-bottom:7px; border-bottom:1px solid #574e63; }
      .parity-tab { border:1px solid #574e63; border-bottom:0; background:#29252e; color:#bbb4c3; padding:6px 10px; cursor:pointer; font:inherit; font-size:12px; }
      .parity-tab.active { background:#564169; color:#fff; }
      .parity-tab-panel[hidden] { display:none !important; }
      .parity-textarea { width:100%; min-height:120px; resize:vertical; box-sizing:border-box; font-family:ui-monospace,SFMono-Regular,Consolas,monospace; font-size:11px; background:#171519; color:#eee; border:1px solid #554b61; padding:7px; }
      .parity-log { min-height:190px; max-height:360px; overflow:auto; white-space:pre-wrap; background:#151316; border:1px solid #504758; padding:7px; font-family:ui-monospace,SFMono-Regular,Consolas,monospace; font-size:10.5px; }
      .parity-status { color:#bcb4c7; font-size:10.5px; min-height:14px; }
      .parity-danger { color:#ff9e9e; }
      .parity-good { color:#9cf3bb; }
      .parity-checkboxes { display:flex; flex-wrap:wrap; gap:6px 11px; }
      .parity-checkboxes label { display:flex; gap:4px; align-items:center; font-size:11px; }
      .parity-split { display:grid; grid-template-columns:minmax(180px,.75fr) minmax(260px,1.25fr); gap:8px; min-height:220px; }
      .parity-actions { display:flex; flex-wrap:wrap; gap:5px; margin-top:6px; }
      .parity-pill { display:inline-block; padding:1px 5px; border:1px solid #675978; border-radius:10px; color:#cbbbdc; font-size:9.5px; }
      .parity-color-input { display:grid; grid-template-columns:42px 1fr; gap:5px; align-items:center; }
      .parity-color-input input[type=color] { width:42px; height:26px; border:0; background:transparent; }
      .parity-row { display:flex; align-items:center; gap:6px; flex-wrap:wrap; }
      .parity-fill { flex:1 1 auto; }
      .parity-mono { font-family:ui-monospace,SFMono-Regular,Consolas,monospace; }
      .parity-card,.parity-list,.parity-textarea,.parity-log { background:var(--skua-bg-3)!important; border-color:var(--skua-border)!important; color:var(--skua-text)!important; }
      .parity-list-row,.parity-option-label,.parity-section-title,.parity-status,.parity-option-desc,.parity-tabs,.parity-tab,.parity-pill { color:var(--skua-text)!important; }
      .parity-list-row:hover { background:color-mix(in srgb, var(--skua-purple) 18%, var(--skua-bg-2))!important; }
      .parity-list-row.selected,.parity-tab.active { background:color-mix(in srgb, var(--skua-purple) 38%, var(--skua-bg-2))!important; color:var(--skua-text)!important; }
      .skua-menu-dropdown { background:var(--skua-bg-3)!important; border-color:var(--skua-border)!important; }
      .skua-menu-item { color:var(--skua-text)!important; }
      .skua-menu-item:hover { background:color-mix(in srgb, var(--skua-purple) 28%, var(--skua-bg-2))!important; }
      @media(max-width:700px){
        .parity-grid,.parity-grid.three,.parity-split,.skills-editor-grid,.skills-editor-bottom{grid-template-columns:1fr;}
      }
    `;
    document.head.appendChild(style);
  }

  function menuGroup(label, items) {
    return `<div class="skua-menu-group"><button class="skua-menu-button parity-menu-toggle" type="button">${esc(label)}</button><div class="skua-menu-dropdown">${items.map(([name,id]) => `<button class="skua-menu-item" type="button" data-open-window="${esc(id)}">${esc(name)}</button>`).join('')}</div></div>`;
  }

  function installMenu() {
    const bar = $('skua-menu-bar');
    if (!bar) return;
    bar.innerHTML = `
      <div class="skua-app-title"><span class="skua-app-dot"></span><span>Skua - Linux</span></div>
      <nav class="skua-menu-nav" aria-label="Skua menu">
        <button class="skua-menu-button" type="button" data-open-window="window-script-loader">Scripts</button>
        ${menuGroup('Options', [
          ['Game','window-game-options'],['Application','window-application-options'],['CoreBots','window-corebots'],['Application Themes','window-themes'],['HotKeys','window-hotkeys']
        ])}
        ${menuGroup('Helpers', [
          ['Runtime','window-runtime'],['Fast Travel','window-fast-travel'],['Current Drops','window-current-drops']
        ])}
        ${menuGroup('Tools', [
          ['Loader','window-loader'],['Grabber','window-grabber'],['Junk Items','window-junk'],['Stats','window-stats'],['Console','window-console']
        ])}
        <button class="skua-menu-button" type="button" data-open-window="window-skills">Skills</button>
        ${menuGroup('Packets', [
          ['Spammer','window-packet-spammer'],['Logger','window-packet-logger'],['Interceptor','window-packet-interceptor']
        ])}
        <button class="skua-menu-button" id="bank-open-button" type="button">Bank</button>
        <button class="skua-menu-button" type="button" data-open-window="window-logs">Logs</button>
        <button class="skua-menu-button" type="button" data-open-window="window-plugins">Plugins</button>
      </nav>
      <div class="skua-top-spacer"></div>
      <div class="skua-backend-state" title="Linux backend connection">
        <span id="skua-backend-dot"></span>
        <span id="skua-backend-label">Backend offline</span>
      </div>
      <button class="skua-backend-restart" id="skua-backend-restart" type="button" title="Restart Skua backend" aria-label="Restart Skua backend">Restart Backend</button>
      <button class="skua-backend-restart skua-ruffle-reload" id="skua-ruffle-reload" type="button" title="Clear Ruffle cache and reload AQW" aria-label="Clear Ruffle cache and reload AQW">Reload Ruffle</button>
      <button class="skua-quick-button" type="button" data-open-window="window-auto">Auto</button>
      <button class="skua-quick-button" type="button" data-open-window="window-jump">Jump</button>
    `;

    bar.addEventListener('click', (event) => {
      const toggle = event.target.closest('.parity-menu-toggle');
      if (toggle) {
        event.stopPropagation();
        const group = toggle.closest('.skua-menu-group');
        qsa('.skua-menu-group.open', bar).forEach((other) => { if (other !== group) other.classList.remove('open'); });
        group.classList.toggle('open');
      } else if (event.target.closest('.skua-menu-item')) {
        qsa('.skua-menu-group.open', bar).forEach((group) => group.classList.remove('open'));
      }
    });
    document.addEventListener('pointerdown', (event) => {
      if (!event.target.closest('.skua-menu-group')) qsa('.skua-menu-group.open', bar).forEach((group) => group.classList.remove('open'));
    });
  }

  function win(id, title, body, size='parity-medium', footer='Skua.Core · Linux adapter') {
    return `<section class="skua-window ${size}" id="${id}" aria-label="${esc(title)}"><div class="skua-window-titlebar"><div class="skua-window-title">${esc(title)}</div><div class="skua-window-controls"><button class="skua-window-control" type="button" data-window-minimize>–</button><button class="skua-window-control" type="button" data-window-close>×</button></div></div><div class="skua-window-body">${body}</div><div class="skua-window-footer">${esc(footer)}</div></section>`;
  }

  function replaceOrAppend(id, markup) {
    const old = $(id);
    if (old) old.outerHTML = markup;
    else $('skua-window-layer')?.insertAdjacentHTML('beforeend', markup);
  }

  function installWindows() {
    for (const id of ['window-options','window-tools','window-packets']) $(id)?.remove();

    replaceOrAppend('window-auto', win('window-auto','Auto', `
      <div class="parity-stack">
        <div class="parity-grid">
          <div><label class="skua-label">Class</label><select class="skua-input" id="auto-class"></select></div>
          <div><label class="skua-label">Class Mode</label><select class="skua-input" id="auto-class-mode"></select></div>
        </div>
        <div><label class="skua-label">Manual Map Monster IDs</label><input class="skua-input parity-mono" id="auto-manual-ids" placeholder="1, 2, 3"></div>
        <div class="parity-card">
          <div class="parity-option-row"><div><div class="parity-option-label">Auto Attack</div><div class="parity-option-desc">Continuously attacks available monsters using the selected class mode.</div></div><button class="skua-switch" id="auto-attack-switch" type="button" role="switch" aria-checked="false"></button></div>
          <div class="parity-option-row"><div><div class="parity-option-label">Auto Hunt</div><div class="parity-option-desc">Hunts monsters across map cells using Skua.Core Auto Hunt.</div></div><button class="skua-switch" id="auto-hunt-switch" type="button" role="switch" aria-checked="false"></button></div>
        </div>
        <div class="parity-status" id="auto-status">Stopped.</div>
      </div>`, 'parity-small'));

    replaceOrAppend('window-fast-travel', win('window-fast-travel','Fast Travel', `
      <div class="parity-split">
        <div>
          <div class="parity-toolbar"><input class="skua-input parity-fill" id="fast-search" type="search" placeholder="Search"><button class="skua-button" id="fast-clear" type="button">Clear</button></div>
          <div class="parity-list" id="fast-list"></div>
        </div>
        <div class="parity-stack">
          <div><label class="skua-label">Description Name</label><input class="skua-input" id="fast-name"></div>
          <div><label class="skua-label">Map Name</label><input class="skua-input" id="fast-map"></div>
          <div class="parity-grid"><div><label class="skua-label">Cell</label><input class="skua-input" id="fast-cell" value="Enter"></div><div><label class="skua-label">Pad</label><input class="skua-input" id="fast-pad" value="Spawn"></div></div>
          <div class="parity-grid"><label><input id="fast-private-enabled" type="checkbox"> Private room</label><div><label class="skua-label">Private Number</label><input class="skua-input" id="fast-private" type="number" min="0" value="0"></div></div>
          <div class="parity-actions"><button class="skua-button" id="fast-save" type="button">Add</button><button class="skua-button secondary" id="fast-update" type="button">Update</button><button class="skua-button secondary" id="fast-remove" type="button">Remove</button><button class="skua-button secondary" id="fast-current" type="button">Current</button><button class="skua-button" id="fast-go" type="button">Go</button></div>
          <div class="parity-status" id="fast-status"></div>
        </div>
      </div>`, 'parity-wide'));

    replaceOrAppend('window-skills', win('window-skills','Advanced Skills', `
      <div class="parity-stack">
        <details class="parity-card parity-expander" id="skills-editor-expander" open>
          <summary>Create/Edit Skill</summary>
          <div class="parity-expander-content">
            <div class="parity-grid" style="margin-bottom:8px">
              <div class="parity-card"><div class="parity-section-title">Set SkillTimeout</div><input class="skua-input" id="skills-timeout" type="number" min="0" value="100"></div>
              <div class="parity-card"><div class="parity-section-title">Use Mode</div><label><input id="skills-wait-mode" type="checkbox"> Wait for Cooldown</label><br><label><input id="skills-available-mode" type="checkbox" checked> Use if Available</label><br><label><input id="skills-reset-target" type="checkbox"> Reset on Target Change</label></div>
            </div>
            <div class="skills-editor-grid">
              <div class="parity-card">
                <div class="parity-section-title">Skills</div>
                <div class="parity-list" id="skills-current-list" tabindex="0" style="min-height:230px;max-height:330px"></div>
                <div class="parity-actions"><button class="skua-button secondary" id="skills-select-up" title="Select previous">↑</button><button class="skua-button secondary" id="skills-move-up" title="Move selected up">⇈</button><button class="skua-button secondary" id="skills-select-down" title="Select next">↓</button><button class="skua-button secondary" id="skills-move-down" title="Move selected down">⇊</button><button class="skua-button secondary" id="skills-edit-current">Edit</button><button class="skua-button secondary" id="skills-remove-current">Remove</button><button class="skua-button secondary" id="skills-clear-current">Clear Skills</button></div>
              </div>
              <details class="parity-card" id="skills-rule-details" open><summary class="parity-section-title">Use Rules</summary><div id="skills-rule-editor" class="parity-stack" style="margin-top:7px"></div><div class="parity-actions" id="skills-add-buttons"><button class="skua-button" data-add-skill="0">0</button><button class="skua-button" data-add-skill="1">1</button><button class="skua-button" data-add-skill="2">2</button><button class="skua-button" data-add-skill="3">3</button><button class="skua-button" data-add-skill="4">4</button><button class="skua-button" data-add-skill="5" title="Potion">5</button></div></details>
            </div>
            <div class="skills-editor-bottom">
              <div><label class="skua-label">Class Name</label><input class="skua-input" id="skills-class"></div>
              <div><label class="skua-label">Class Use Mode</label><select class="skua-input" id="skills-class-mode"></select></div>
              <button class="skua-button" id="skills-save">Save</button>
            </div>
            <textarea class="parity-textarea" id="skills-dsl" hidden></textarea>
          </div>
        </details>
        <details class="parity-card parity-expander" id="skills-saved-expander" open>
          <summary>Saved Skills</summary>
          <div class="parity-expander-content">
            <div class="parity-toolbar"><input class="skua-input parity-fill" id="skills-search" type="search" placeholder="Search saved skills"><button class="skua-button" id="skills-reload">Reload</button></div>
            <div class="parity-list" id="skills-list" style="max-height:280px"></div>
            <div class="parity-actions"><button class="skua-button secondary" id="skills-remove">Remove Saved</button><button class="skua-button secondary" id="skills-reset">Reset Sets</button><button class="skua-button secondary" id="skills-sync">Sync</button></div>
          </div>
        </details>
        <div class="parity-status" id="skills-status"></div>
      </div>`, 'parity-wide'));

    replaceOrAppend('window-logs', win('window-logs','Logs', `
      <div class="parity-tabs"><button class="parity-tab active" data-log-type="Debug">Debug</button><button class="parity-tab" data-log-type="Script">Script</button><button class="parity-tab" data-log-type="Flash">Flash</button></div>
      <div class="parity-toolbar"><button class="skua-button" id="logs-refresh">Refresh</button><button class="skua-button secondary" id="global-log-clear">Clear</button><button class="skua-button secondary" id="global-log-copy">Copy</button><button class="skua-button secondary" id="global-log-save">Save</button></div>
      <div class="parity-log" id="global-log"></div>
      <div class="parity-status" id="logs-status"></div>
    `, 'parity-wide'));

    replaceOrAppend('window-plugins', win('window-plugins','Plugins', `
      <div class="parity-toolbar"><input class="skua-input parity-fill" id="plugins-search" type="search" placeholder="Search"><button class="skua-button" id="plugins-load">Load</button><button class="skua-button secondary" id="plugins-unload-all">Unload All</button></div>
      <div class="parity-list" id="plugins-list"></div>
      <div class="parity-status" id="plugins-status"></div>
    `, 'parity-medium'));

    const newWindows = [
      win('window-script-options','Edit Script Options', `<div class="parity-toolbar"><button class="skua-button" id="script-options-refresh">Refresh</button><button class="skua-button secondary" id="script-options-defaults">Defaults</button></div><div id="script-options-body" class="parity-stack"></div><div class="parity-status" id="script-options-status"></div>`, 'parity-medium'),
      win('window-game-options','Game Options', `<div class="parity-toolbar"><button class="skua-button" id="game-options-save">Save</button><button class="skua-button secondary" id="game-options-reset">Reset</button><button class="skua-button secondary" id="game-options-default">Default</button><button class="skua-button secondary" id="game-options-reload-map">Reload Map</button></div><div class="parity-card"><div class="parity-option-row"><div class="parity-option-label">Upgrade Name Color</div><input id="game-upgrade" type="checkbox"></div><div class="parity-option-row"><div class="parity-option-label">Staff Name Color</div><input id="game-staff" type="checkbox"></div></div><div id="game-options-body" class="parity-stack"></div><div class="parity-status" id="game-options-status"></div>`, 'parity-wide'),
      win('window-application-options','Application Options', `<div class="parity-card"><div class="parity-section-title">Ruffle / Client Cache</div><div class="parity-option-desc">Clear Client Cache removes cached HTTP game assets without resetting persistent browser storage. Clear Ruffle Cache & Reload also reloads the AQW/Ruffle renderer so its current WASM/AVM2 runtime is recreated.</div><div class="parity-actions"><button class="skua-button secondary" id="application-clear-cache">Clear Client Cache</button><button class="skua-button" id="application-reload-ruffle">Clear Ruffle Cache & Reload</button></div></div><div id="application-options-body" class="parity-stack"></div><div class="parity-status" id="application-options-status"></div>`, 'parity-medium'),
      win('window-corebots','CoreBots Options', `<div class="parity-tabs"><button class="parity-tab active" data-cbo-tab="options">Options</button><button class="parity-tab" data-cbo-tab="other">Other</button><button class="parity-tab" data-cbo-tab="loadout">Loadout</button></div><div class="parity-row" style="justify-content:flex-end;margin-bottom:6px"><span class="parity-status parity-fill" id="corebots-player"></span><button class="skua-button secondary" id="corebots-load">Load</button><button class="skua-button" id="corebots-save">Save</button></div><div id="corebots-options-panel"></div><div id="corebots-other-panel" hidden></div><div id="corebots-loadout-panel" hidden></div><div class="parity-status" id="corebots-status"></div>`, 'parity-wide'),
      win('window-themes','Application Themes', `<div class="parity-split"><div><div class="parity-list" id="themes-list"></div><div class="parity-actions"><button class="skua-button" id="themes-apply">Apply</button><button class="skua-button secondary" id="themes-remove">Remove User Theme</button></div></div><div class="parity-stack"><div class="parity-grid"><div><label class="skua-label">Name</label><input class="skua-input" id="theme-name"></div><div><label class="skua-label">Base Theme</label><select class="skua-input" id="theme-base"><option>Dark</option><option>Light</option></select></div></div><div id="theme-colors" class="parity-grid"></div><label><input id="theme-adjust" type="checkbox"> Use Color Adjustment</label><div class="parity-grid three"><div><label class="skua-label">Contrast Ratio</label><input class="skua-input" id="theme-ratio" type="number" step="0.1" value="4.5"></div><div><label class="skua-label">Contrast</label><select class="skua-input" id="theme-contrast"><option>Low</option><option selected>Medium</option><option>High</option></select></div><div><label class="skua-label">Selection</label><select class="skua-input" id="theme-selection"><option>All</option><option>Primary</option><option>Secondary</option></select></div></div><button class="skua-button" id="theme-save">Save Theme</button><hr><div><label class="skua-label">Game Background</label><select class="skua-input" id="theme-background"></select></div><div class="parity-actions"><button class="skua-button" id="theme-background-apply">Apply Background</button><button class="skua-button secondary" id="theme-background-import">Import SWF</button><button class="skua-button secondary" id="theme-background-folder">Folder</button><button class="skua-button secondary" id="theme-background-repo">Repository</button></div></div></div><div class="parity-status" id="themes-status"></div>`, 'parity-wide'),
      win('window-hotkeys','HotKeys', `<div class="parity-note skua-note">Click a field, then press the key combination. Backspace clears it.</div><div id="hotkeys-body" class="parity-stack"></div><div class="parity-actions"><button class="skua-button" id="hotkeys-save">Save</button></div><div class="parity-status" id="hotkeys-status"></div>`, 'parity-small'),
      win('window-runtime','Runtime', `<div class="parity-tabs"><button class="parity-tab active" data-runtime-tab="drops">To Pickup Drops</button><button class="parity-tab" data-runtime-tab="quests">Registered Quests</button><button class="parity-tab" data-runtime-tab="boosts">Boosts</button></div><div id="runtime-drops-panel"></div><div id="runtime-quests-panel" hidden></div><div id="runtime-boosts-panel" hidden></div><div class="parity-actions"><button class="skua-button secondary" data-open-window="window-notify-drop">Notify Drop…</button></div><div class="parity-status" id="runtime-status"></div>`, 'parity-medium'),
      win('window-notify-drop','Notify Drop', `<div class="parity-toolbar"><input class="skua-input parity-fill" id="notify-input" placeholder="Item name | Another item"><button class="skua-button" id="notify-add">Add</button></div><div class="parity-list" id="notify-list"></div><div class="parity-grid"><div><label class="skua-label">Sound Count</label><input class="skua-input" id="notify-count" type="number" min="1" value="5"></div><div><label class="skua-label">Sound Delay</label><input class="skua-input" id="notify-delay" type="number" min="0" value="200"></div></div><div class="parity-actions"><button class="skua-button secondary" id="notify-remove">Remove Selected</button><button class="skua-button secondary" id="notify-clear">Clear</button><button class="skua-button" id="notify-test">Test</button></div><div class="parity-status" id="notify-status"></div>`, 'parity-small'),
      win('window-current-drops','Current Drops', `<div class="parity-toolbar"><button class="skua-button" id="current-drops-pick">Pick Selected</button><button class="skua-button secondary" id="current-drops-all">Pickup All</button><button class="skua-button secondary" id="current-drops-ac">Pickup AC</button><button class="skua-button secondary" id="current-drops-refresh">Refresh</button></div><div class="parity-list" id="current-drops-list"></div><div class="parity-status" id="current-drops-status"></div>`, 'parity-medium'),
      win('window-loader','Loader', `<div class="parity-grid"><div><label class="skua-label">Type</label><select class="skua-input" id="loader-type"><option value="0">Shop</option><option value="1">Quest</option></select></div><div><label class="skua-label">ID(s)</label><input class="skua-input" id="loader-ids" placeholder="1234 or 1,2,3"></div></div><button class="skua-button" id="loader-load">Load</button><hr><div class="parity-toolbar"><button class="skua-button" id="loader-quest-file">Get Quest Data</button><button class="skua-button secondary" id="loader-update">Update</button><button class="skua-button secondary" id="loader-update-all">All</button><button class="skua-button secondary" id="loader-range">Range</button><button class="skua-button secondary" id="loader-cancel">Cancel</button></div><div class="parity-toolbar"><input class="skua-input parity-fill" id="loader-search" type="search" placeholder="Search quests"><button class="skua-button secondary" id="loader-copy-ids">Copy IDs</button><button class="skua-button secondary" id="loader-copy-names">Copy Names</button><button class="skua-button secondary" id="loader-copy-both">Copy Both</button></div><div class="parity-list" id="loader-quest-list"></div><div class="parity-actions"><button class="skua-button" id="loader-selected-load">Load Selected</button><button class="skua-button secondary" id="loader-fake">Fake Complete</button></div><div class="parity-status" id="loader-status"></div>`, 'parity-wide'),
      win('window-grabber','Grabber', `<div class="parity-toolbar"><select class="skua-input" id="grabber-type"><option>Shop Items</option><option>Shop IDs</option><option>Quests</option><option>Inventory Items</option><option>House Inventory Items</option><option>Temp Inventory Items</option><option>Bank Items</option><option>Cell Monsters</option><option>Map Monsters</option><option>GetMap Item IDs</option></select><input class="skua-input parity-fill" id="grabber-search" type="search" placeholder="Search"><button class="skua-button" id="grabber-refresh">Refresh</button></div><div class="parity-list" id="grabber-list"></div><div class="parity-actions" id="grabber-actions"></div><div class="parity-status" id="grabber-status"></div>`, 'parity-wide'),
      win('window-junk','Junk Items', `<div class="parity-toolbar"><input class="skua-input parity-fill" id="junk-search" type="search" placeholder="Search"><button class="skua-button secondary" id="junk-refresh">Refresh</button><button class="skua-button secondary" id="junk-clear">Unmark All Junk</button><button class="skua-button" id="junk-sell">Sell All Junk</button></div><label><input id="junk-skip-warning" type="checkbox"> Skip sell warning</label><div class="parity-list" id="junk-list"></div><div class="parity-status" id="junk-status"></div>`, 'parity-medium'),
      win('window-stats','Stats', `<div class="parity-toolbar"><button class="skua-button secondary" id="stats-refresh">Refresh</button><button class="skua-button secondary" id="stats-space">Get Space</button><button class="skua-button" id="stats-reset">Reset</button></div><div class="parity-grid three" id="stats-grid"></div><div class="parity-status" id="stats-status"></div>`, 'parity-medium'),
      win('window-console','Console', `<textarea class="parity-textarea" id="console-code">Bot.Log("Test");</textarea><div class="parity-actions"><button class="skua-button" id="console-run">Run</button></div><div class="parity-status" id="console-status"></div>`, 'parity-medium'),
      win('window-plugin-options','Plugin Options', `<div id="plugin-options-body" class="parity-stack"></div><div class="parity-status" id="plugin-options-status"></div>`, 'parity-medium'),
      win('window-packet-spammer','Packet Spammer', `<div class="parity-grid"><label><input id="spammer-client" type="checkbox"> Send To Client</label><div><label class="skua-label">Spam Delay</label><input class="skua-input" id="spammer-delay" type="number" min="1" value="1000"></div></div><textarea class="parity-textarea" id="spammer-packet" placeholder="Packet"></textarea><div class="parity-actions"><button class="skua-button" id="spammer-add">Add</button><button class="skua-button secondary" id="spammer-send">Send Once</button><button class="skua-button secondary" id="spammer-load">Load</button><button class="skua-button secondary" id="spammer-save">Save</button></div><div class="parity-list" id="spammer-list"></div><div class="parity-actions"><button class="skua-button" id="spammer-toggle">Start</button><button class="skua-button secondary" id="spammer-remove">Remove</button><button class="skua-button secondary" id="spammer-clear">Clear</button></div><div class="parity-status" id="spammer-status"></div>`, 'parity-medium'),
      win('window-packet-logger','Packet Logger', `<div class="parity-toolbar"><label><input id="packet-logger-enabled" type="checkbox"> Receiving</label><input class="skua-input parity-fill" id="packet-logger-search" type="search" placeholder="Search"><button class="skua-button secondary" id="packet-logger-clear">Clear</button><button class="skua-button secondary" id="packet-logger-save">Save</button></div><div class="parity-checkboxes" id="packet-logger-filters"></div><div class="parity-log" id="packet-logger-log"></div><div class="parity-status" id="packet-logger-status"></div>`, 'parity-wide'),
      win('window-packet-interceptor','Packet Interceptor', `<div class="parity-toolbar"><select class="skua-input" id="interceptor-server" style="min-width:170px"><option value="">Select Server</option></select><label><input id="interceptor-enabled" type="checkbox"> Connect Interceptor</label><label><input id="interceptor-logging" type="checkbox" checked> Log Packets</label><input class="skua-input parity-fill" id="interceptor-search" type="search" placeholder="Search"><button class="skua-button secondary" id="interceptor-clear-filters">Clear Filters</button><button class="skua-button secondary" id="interceptor-clear">Clear Packets</button></div><div class="parity-checkboxes" id="interceptor-filters"></div><div class="parity-log" id="interceptor-log"></div><div class="parity-status" id="interceptor-status"></div>`, 'parity-wide')
    ];
    $('skua-window-layer')?.insertAdjacentHTML('beforeend', newWindows.join(''));
  }

  function installScriptOptionsButton() {
    const button = $('script-options-button');
    if (!button) return;
    button.removeAttribute('disabled');
    button.textContent = 'Edit Script Options';
    button.dataset.openWindow = 'window-script-options';
  }

  function installRuleEditorMarkup() {
    const host = $('skills-rule-editor');
    if (!host) return;
    host.innerHTML = `
      <div class="parity-grid three"><div><label class="skua-label">Skill #</label><input class="skua-input" id="rule-skill" type="number" min="0" value="1"></div><div><label class="skua-label">Wait (ms)</label><input class="skua-input" id="rule-wait" type="number" value="0"></div><label><input id="rule-use" type="checkbox"> Use rules</label></div>
      <div class="parity-grid three"><div><label class="skua-label">HP value</label><input class="skua-input" id="rule-hp" type="number" value="0"></div><label><input id="rule-hp-greater" type="checkbox" checked> HP greater</label><label><input id="rule-hp-percent" type="checkbox" checked> HP %</label></div>
      <div class="parity-grid three"><div><label class="skua-label">MP value</label><input class="skua-input" id="rule-mp" type="number" value="0"></div><label><input id="rule-mp-greater" type="checkbox" checked> MP greater</label><label><input id="rule-mp-percent" type="checkbox" checked> MP %</label></div>
      <div class="parity-grid three"><div><label class="skua-label">Aura</label><input class="skua-input" id="rule-aura"></div><div><label class="skua-label">Aura value</label><input class="skua-input" id="rule-aura-value" type="number" step="0.1" value="0"></div><div><label class="skua-label">Aura target index</label><input class="skua-input" id="rule-aura-target" type="number" value="0"></div></div>
      <div class="parity-row"><label><input id="rule-aura-greater" type="checkbox" checked> Aura greater</label><label><input id="rule-skip" type="checkbox"> Skip use</label></div>
      <div class="parity-grid three"><div><label class="skua-label">Party HP</label><input class="skua-input" id="rule-party-hp" type="number" value="0"></div><label><input id="rule-party-greater" type="checkbox" checked> Party HP greater</label><label><input id="rule-party-percent" type="checkbox" checked> Party HP %</label></div>
      <div class="parity-row"><label><input id="rule-multi" type="checkbox"> Multi Aura</label><div><label class="skua-label">Operator index</label><input class="skua-input" id="rule-multi-op" type="number" value="0" style="width:70px"></div></div>
      <div><label class="skua-label">Multi Aura checks (one per line: name|stack|greater|targetIndex)</label><textarea class="parity-textarea" id="rule-multi-lines" style="min-height:65px"></textarea></div>
      <div class="parity-actions"><button class="skua-button" id="rule-build">Apply Rule</button><button class="skua-button secondary" id="rule-parse">Parse Selected Skill</button></div>
      <div class="parity-status" id="rule-status"></div>`;
  }

  document.addEventListener('DOMContentLoaded', () => {
    installStyles();
    installMenu();
    installWindows();
    installScriptOptionsButton();
    installRuleEditorMarkup();
    waitForApi();
  });

  function waitForApi() {
    if (window.skuaLinuxUiApi) {
      state.api = window.skuaLinuxUiApi;
      bindFunctionalUi();
      return;
    }
    setTimeout(waitForApi, 40);
  }

  function cmd(name, args=[], timeout=15000) {
    return state.api.sendBackendCommand(name, args, timeout);
  }
  function shell(name, args=[], timeout=15000) {
    return state.api.sendShellCommand(name, args, timeout);
  }
  function toast(message, tone='good') { state.api.showToast(message, tone); }
  function log(message, level='info') { state.api.appendLog(message, level); }
  function backend() {
    return typeof state.api.isBackendReady === 'function'
      ? state.api.isBackendReady()
      : state.api.isBackendConnected();
  }
  function val(id) { return $(id)?.value ?? ''; }
  function checked(id) { return Boolean($(id)?.checked); }
  function setStatus(id, text, good=false) { const el=$(id); if(el){el.textContent=text; el.classList.toggle('parity-good',good);} }
  function switchState(id, on) { const el=$(id); if(el) el.setAttribute('aria-checked', String(Boolean(on))); }

  function parseBool(value) {
    if (typeof value === 'boolean') return value;
    return String(value).toLowerCase() === 'true';
  }
  function human(name) { return String(name).replace(/([a-z0-9])([A-Z])/g,'$1 $2').replaceAll('_',' '); }

  function bindFunctionalUi() {
    bindMenus();
    bindScriptOptions();
    bindGameOptions();
    bindApplicationOptions();
    bindCoreBots();
    bindThemes();
    bindHotKeys();
    bindRuntime();
    bindNotify();
    bindFastTravel();
    bindCurrentDrops();
    bindLoader();
    bindGrabber();
    bindJunk();
    bindStats();
    bindConsole();
    bindAuto();
    bindSkills();
    bindPacketSpammer();
    bindPacketLogger();
    bindPacketInterceptor();
    bindLogs();
    bindPlugins();
    bindJumpParity();
    bindHotKeyExecution();
    startNotifyPoll();

    window.addEventListener('skua-backend-ready', () => {
      // Theme is application-wide state, so apply the current saved theme even
      // when the Themes window itself is closed.
      const themeRefresh = refreshers['window-themes'];
      if (themeRefresh) setTimeout(() => void themeRefresh(), 0);

      for (const [id, refresh] of Object.entries(refreshers)) {
        if (id !== 'window-themes' && $(id)?.classList.contains('open')) {
          setTimeout(() => void refresh(), 0);
        }
      }
    });
  }

  function bindMenus() {
    // New windows are handled by the original initializeWindows because they
    // exist before its DOMContentLoaded callback runs. This hook only refreshes
    // each window on open.
    document.addEventListener('click', (event) => {
      const button = event.target.closest('[data-open-window]');
      if (!button) return;
      const id = button.dataset.openWindow;
      const refresh = refreshers[id];
      if (refresh) setTimeout(() => void refresh(), 0);
    });
  }

  const refreshers = {};

  function makeOptionControl(option, onChange) {
    let control;
    const type = String(option.type ?? 'String');
    const value = option.value ?? '';
    if (option.isEnum) {
      control = document.createElement('select');
      control.className = 'skua-input';
      for (const item of option.enumValues ?? []) {
        const opt = document.createElement('option');
        opt.textContent = item;
        opt.value = item;
        if (String(value).replaceAll('_',' ') === item) opt.selected = true;
        control.appendChild(opt);
      }
    } else if (/Boolean|bool/i.test(type)) {
      control = document.createElement('input');
      control.type = 'checkbox';
      control.checked = parseBool(value);
    } else {
      control = document.createElement('input');
      control.className = 'skua-input';
      control.type = /Int32|int/i.test(type) ? 'number' : 'text';
      control.value = value ?? '';
    }
    if (option.transient) control.disabled = true;
    control.addEventListener('change', () => {
      const newValue = control.type === 'checkbox' ? control.checked : control.value;
      void onChange(newValue, control);
    });
    return control;
  }

  function renderOptionGroups(host, options, onChange) {
    host.textContent = '';
    const groups = new Map();
    for (const option of options ?? []) {
      const category = option.category ?? 'Options';
      if (!groups.has(category)) groups.set(category, []);
      groups.get(category).push(option);
    }
    for (const [category, items] of groups) {
      const card = document.createElement('div');
      card.className = 'parity-card';
      card.innerHTML = `<div class="parity-section-title">${esc(category)}</div>`;
      for (const option of items) {
        const row = document.createElement('div');
        row.className = 'parity-option-row';
        const copy = document.createElement('div');
        copy.innerHTML = `<div class="parity-option-label">${esc(option.displayName ?? option.name)}</div>${option.description ? `<div class="parity-option-desc">${esc(option.description)}</div>` : ''}`;
        row.appendChild(copy);
        row.appendChild(makeOptionControl(option, (value, control) => onChange(option, value, control)));
        card.appendChild(row);
      }
      host.appendChild(card);
    }
  }

  function bindScriptOptions() {
    async function refresh() {
      if (!backend()) return setStatus('script-options-status','Backend offline.');
      setStatus('script-options-status','Loading script configuration…');
      try {
        const data = await cmd('script.options.status', [], 30000);
        if (!data.available) {
          $('script-options-body').innerHTML = '<div class="skua-note">The loaded script does not expose configurable options.</div>';
          return setStatus('script-options-status','No script options.');
        }
        renderOptionGroups($('script-options-body'), data.options, async (option, value, control) => {
          control.disabled = true;
          try {
            await cmd('script.options.set', [option.category, option.name, String(value)]);
            setStatus('script-options-status',`Saved ${option.displayName ?? option.name}.`,true);
          } catch (error) { setStatus('script-options-status',String(error?.message ?? error)); }
          finally { if (!option.transient) control.disabled = false; }
        });
        setStatus('script-options-status', data.optionsFile ? `Storage: ${data.optionsFile}` : 'Loaded.', true);
      } catch (error) {
        $('script-options-body').textContent = '';
        setStatus('script-options-status',`Options error: ${error?.message ?? error}`);
      }
    }
    refreshers['window-script-options'] = refresh;
    $('script-options-refresh')?.addEventListener('click', () => void refresh());
    $('script-options-defaults')?.addEventListener('click', async () => {
      try { await cmd('script.options.defaults'); await refresh(); toast('Script options reset to defaults.'); }
      catch (error) { setStatus('script-options-status',String(error?.message ?? error)); }
    });
  }

  function bindGameOptions() {
    async function refresh() {
      if (!backend()) return setStatus('game-options-status','Backend offline.');
      try {
        const data = await cmd('options.game.status');
        const host = $('game-options-body');
        host.textContent = '';
        const card = document.createElement('div');
        card.className = 'parity-card';
        for (const option of data.options ?? []) {
          const row = document.createElement('div');
          row.className = 'parity-option-row';
          const label = document.createElement('div');
          label.innerHTML = `<div class="parity-option-label">${esc(option.displayName ?? option.name)}${option.suffix ? ` (${esc(option.suffix)})` : ''}</div>`;
          const control = document.createElement('input');
          if (/Boolean/i.test(option.type)) { control.type='checkbox'; control.checked=Boolean(option.value); }
          else { control.className='skua-input'; control.type=/Int/i.test(option.type)?'number':'text'; control.value=option.value ?? ''; }
          control.addEventListener('change', async () => {
            try {
              await cmd('options.game.set',[option.name, control.type==='checkbox'?control.checked:control.value]);
              setStatus('game-options-status',`${option.displayName ?? option.name} updated.`,true);
            } catch (error) { setStatus('game-options-status',String(error?.message ?? error)); }
          });
          row.append(label,control); card.appendChild(row);
        }
        const server = document.createElement('div');
        server.className='parity-option-row';
        server.innerHTML='<div class="parity-option-label">Relogin Server</div>';
        const select=document.createElement('select'); select.className='skua-input';
        for(const name of data.servers??[]){const o=document.createElement('option');o.value=name;o.textContent=name;if(name===data.selectedServer)o.selected=true;select.appendChild(o);}
        select.addEventListener('change',()=>void cmd('options.game.set',['ReloginServer',select.value]).catch(e=>setStatus('game-options-status',String(e?.message??e))));
        server.appendChild(select);card.appendChild(server);host.appendChild(card);
        setStatus('game-options-status','Options loaded.',true);
      } catch(error){setStatus('game-options-status',`Game options error: ${error?.message??error}`);}
    }
    refreshers['window-game-options']=refresh;
    $('game-options-save')?.addEventListener('click',()=>void cmd('options.game.save').then(()=>toast('Game options saved.')).catch(e=>setStatus('game-options-status',String(e?.message??e))));
    $('game-options-reset')?.addEventListener('click',()=>void cmd('options.game.reset').then(refresh).catch(e=>setStatus('game-options-status',String(e?.message??e))));
    $('game-options-default')?.addEventListener('click',()=>void cmd('options.game.default').then(refresh).catch(e=>setStatus('game-options-status',String(e?.message??e))));
    $('game-options-reload-map')?.addEventListener('click',()=>void cmd('options.game.reloadMap').then(()=>toast('Map reloaded.')).catch(e=>setStatus('game-options-status',String(e?.message??e))));
    $('game-upgrade')?.addEventListener('change',()=>void cmd('options.game.upgrade',[checked('game-upgrade')]).catch(e=>setStatus('game-options-status',String(e?.message??e))));
    $('game-staff')?.addEventListener('change',()=>void cmd('options.game.staff',[checked('game-staff')]).catch(e=>setStatus('game-options-status',String(e?.message??e))));
  }

  function bindApplicationOptions() {
    async function refresh() {
      if (!backend()) return setStatus('application-options-status','Backend offline.');
      try {
        const data=await cmd('options.application.status');
        const host=$('application-options-body');host.textContent='';
        const card=document.createElement('div');card.className='parity-card';
        for(const option of data.options??[]){
          const row=document.createElement('div');row.className='parity-option-row';
          const copy=document.createElement('div');copy.innerHTML=`<div class="parity-option-label">${esc(option.name)}${option.suffix?` (${esc(option.suffix)})`:''}</div><div class="parity-option-desc">${esc(option.description??'')}</div>`;
          const control=document.createElement('input');
          if(/Boolean/i.test(option.type)){control.type='checkbox';control.checked=Boolean(option.value);} else {control.className='skua-input';control.type='number';control.min='1';control.value=option.value??30;}
          control.addEventListener('change',async()=>{
            try{
              await cmd('options.application.set',[option.key,control.type==='checkbox'?control.checked:Number(control.value)]);
              if(option.key==='ShowUsernameInTitle') document.title=control.checked&&data.username?`Skua - ${data.username}`:'Skua';
              setStatus('application-options-status',`${option.name} saved.`,true);
            }catch(error){setStatus('application-options-status',String(error?.message??error));}
          });
          row.append(copy,control);card.appendChild(row);
          if(option.key==='ShowUsernameInTitle'&&Boolean(option.value)&&data.username) document.title=`Skua - ${data.username}`;
        }
        host.appendChild(card);setStatus('application-options-status','Application settings loaded.',true);
      }catch(error){setStatus('application-options-status',`Application options error: ${error?.message??error}`);}
    }
    refreshers['window-application-options']=refresh;
    $('application-clear-cache')?.addEventListener('click',async()=>{
      try{
        setStatus('application-options-status','Clearing client cache...');
        const data=await shell('shell.clear-client-cache',[],30000);
        const before=Number(data?.cacheBytesBefore);
        const after=Number(data?.cacheBytesAfter);
        const detail=Number.isFinite(before)&&Number.isFinite(after)
          ? ` (${Math.round(before/1048576)} MB → ${Math.round(after/1048576)} MB)`
          : '';
        setStatus('application-options-status',`Client cache cleared${detail}.`,true);
        toast(`Client cache cleared${detail}.`);
      }
      catch(error){setStatus('application-options-status',String(error?.message??error));}
    });

    $('application-reload-ruffle')?.addEventListener('click',async()=>{
      const confirmed=window.confirm(
        'Clear the Ruffle/client cache and reload AQW?\n\n' +
        'This recreates the current Ruffle runtime. Persistent browser storage is preserved, but the current AQW session/game state will reload. Running scripts may be interrupted.'
      );
      if(!confirmed)return;

      try{
        setStatus('application-options-status','Clearing Ruffle cache and reloading AQW...');
        await shell('shell.clear-ruffle-cache-and-reload',[],30000);
        toast('Ruffle cache cleared. Reloading AQW...');
      }
      catch(error){setStatus('application-options-status',String(error?.message??error));}
    });
  }

  function bindCoreBots() {
    let data = null;
    const optionLabels = {
      PrivateRooms:'Private Rooms', PublicDifficult:'Public on Difficult Parts', BankMiscAC:'Bank Misc. AC items on Start-Up', LoggerInChat:'Logger in Chat',
      MessageBoxCheck:'Force Off MessageBoxes', RestCheck:'Should Rest', DisableAutoEnhance:'Disable AutoEnhance', DisableBestGear:'Disable BestGear', AntiLag:'Anti Lag', IncognitoMode:'Incognito Mode',
      PrivateRoomNr:'Room Number', ActionDelayNr:'Action Delay', ExitCombatNr:'Exit Combat Delay', HuntDelayNr:'Hunt Delay', QuestTriesNr:'Accept and Complete Tries', QuestMaxNr:'Loaded Quests Max.', StopLocationSelect:'Map after stopping the Bot'
    };
    const optionKeys = Object.keys(optionLabels);
    const otherGroups = [
      ['Boosters',['doGoldBoost','doClassBoost','doRepBoost','doExpBoost']],
      ['Nation Farms',['Nation_SellMemVoucher','Nation_ReturnPolicyDuringSupplies','UltraAlteonForSupplies']],
      ['Bludrut Brawl (PvP)',['PvP_SoloPvPBoss']],
      ['Bot Creators Only',['BCO_Story_TestBot']]
    ];
    const otherLabels = {
      doGoldBoost:'Use Gold Boosts when farming gold', doClassBoost:'Use Class Boosts when ranking up a class', doRepBoost:'Use Reputation Boosts when farming REP', doExpBoost:'Use Experience Boosts when farming EXP',
      Nation_SellMemVoucher:'Sell Voucher of Nulgath if not needed', Nation_ReturnPolicyDuringSupplies:'Do Swindles Return during Supplies', UltraAlteonForSupplies:'Ultra Alteon for Supplies',
      PvP_SoloPvPBoss:'Kill ads before boss', BCO_Story_TestBot:'Story Bot Test Mode'
    };

    function cboInput(key, value, values = null) {
      const def = data?.defaults?.[key];
      if (typeof def === 'boolean') return `<input data-cbo-key="${esc(key)}" type="checkbox" ${parseBool(value)?'checked':''}>`;
      if (Array.isArray(values)) return `<select class="skua-input" data-cbo-key="${esc(key)}">${['',...values].map(v=>`<option ${String(v)===String(value)?'selected':''}>${esc(v)}</option>`).join('')}</select>`;
      return `<input class="skua-input" data-cbo-key="${esc(key)}" ${typeof def==='number'?'type="number"':''} value="${esc(value)}">`;
    }

    function render() {
      if (!data) return;
      const values=data.values??{};
      $('corebots-options-panel').innerHTML = `<div class="parity-card">${optionKeys.map(key=>`<div class="parity-option-row"><div class="parity-option-label">${esc(optionLabels[key])}</div>${cboInput(key,values[key])}</div>`).join('')}</div>`;
      $('corebots-other-panel').innerHTML = otherGroups.map(([title,keys])=>`<div class="parity-card"><div class="parity-section-title">${esc(title)}</div>${keys.map(key=>`<div class="parity-option-row"><div class="parity-option-label">${esc(otherLabels[key]??human(key))}</div>${cboInput(key,values[key])}</div>`).join('')}</div>`).join('');
      const roles=[['Solo',1],['Farm',2],['Dodge',3],['Boss',4]];
      const eq=data.equipment??{};
      $('corebots-loadout-panel').innerHTML = roles.map(([role,n])=>{
        const classKey=`${role}ClassSelect`, equipKey=`${role}EquipCheck`, modeKey=`${role}ModeSelect`;
        const currentClass=values[classKey]??'';
        let modes=['Base'];
        const modeSource=data.availableModes?.[currentClass==='[Current]'?'':currentClass];
        if(Array.isArray(modeSource)&&modeSource.length)modes=modeSource;
        return `<div class="parity-card"><div class="parity-section-title">${role}</div><div class="parity-grid"><div><label class="skua-label">Class</label>${cboInput(classKey,currentClass,data.classes??[])}</div><div><label class="skua-label">Mode</label>${cboInput(modeKey,values[modeKey]??'Base',modes)}</div></div><label><input data-cbo-key="${equipKey}" type="checkbox" ${parseBool(values[equipKey])?'checked':''}> Use ${role} Equipment</label><div class="parity-grid three">${[
          ['Helm',eq.helms],['Armor',eq.armors],['Cape',eq.capes],['Weapon',eq.weapons],['Pet',eq.pets],['GroundItem',eq.groundItems]
        ].map(([kind,list])=>`<div><label class="skua-label">${kind==='GroundItem'?'Ground Item':kind}</label>${cboInput(`${kind}${n}Select`,values[`${kind}${n}Select`]??'',list??[])}</div>`).join('')}</div></div>`;
      }).join('');
    }

    async function refresh(){
      if(!backend())return setStatus('corebots-status','Backend offline.');
      try{data=await cmd('corebots.status');render();if($('corebots-player'))$('corebots-player').textContent=data.username??'';setStatus('corebots-status',`Loaded: ${data.file}`,true);}catch(error){setStatus('corebots-status',`CoreBots error: ${error?.message??error}`);}
    }
    refreshers['window-corebots']=refresh;
    qsa('[data-cbo-tab]').forEach(btn=>btn.addEventListener('click',()=>{
      qsa('[data-cbo-tab]').forEach(x=>x.classList.toggle('active',x===btn));
      for(const name of ['options','other','loadout']) $(`corebots-${name}-panel`).hidden=btn.dataset.cboTab!==name;
    }));
    function collectValues(){const map={};qsa('[data-cbo-key]').forEach(input=>map[input.dataset.cboKey]=input.type==='checkbox'?input.checked:input.value);return map;}
    async function save(showToast=true){
      const map=collectValues();
      if(map.PrivateRooms===false){
        const confirmed=window.confirm('Whilst we do offer the option, we highly recommend staying in private rooms while botting. Bot in public at your own risk.\n Confirm the use of Public Rooms?');
        if(!confirmed){map.PrivateRooms=true;const control=document.querySelector('[data-cbo-key="PrivateRooms"]');if(control)control.checked=true;}
      }
      try{data=await cmd('corebots.save',[map]);render();if(showToast)toast('CoreBots options saved.');setStatus('corebots-status',`Saved for ${data.username}.`,true);return true;}catch(error){setStatus('corebots-status',String(error?.message??error));return false;}
    }
    $('corebots-load')?.addEventListener('click',()=>void refresh());
    $('corebots-save')?.addEventListener('click',()=>void save(true));
    $('window-corebots')?.addEventListener('click',(event)=>{if(event.target.closest('[data-window-close]')&&data)void save(false);});
  }

  function argbToCss(argb) {
    const text=String(argb??'#FFFFFFFF');
    if(/^#[0-9a-f]{8}$/i.test(text)) return `#${text.slice(3)}`;
    if(/^#[0-9a-f]{6}$/i.test(text)) return text;
    return '#607d8b';
  }
  function cssToArgb(css) {
    const text=String(css??'').trim();
    return /^#[0-9a-f]{6}$/i.test(text)?`#FF${text.slice(1).toUpperCase()}`:text;
  }
  function mixHexColor(a, b, weight) {
    const parse = (value) => {
      const hex = String(value ?? '#000000').replace('#','').slice(-6).padStart(6,'0');
      return [0,2,4].map((offset) => Number.parseInt(hex.slice(offset, offset + 2), 16) || 0);
    };
    const aa = parse(a), bb = parse(b);
    const w = Math.max(0, Math.min(1, Number(weight) || 0));
    return `#${aa.map((value,index) => Math.round(value + (bb[index] - value) * w).toString(16).padStart(2,'0')).join('')}`;
  }

  function applyThemeCss(theme) {
    if (!theme) return;
    const root=document.documentElement;
    const dark=String(theme.baseTheme).toLowerCase()!=='light';
    const primary=argbToCss(theme.primary || '#ffa63ee6');
    const secondary=argbToCss(theme.secondary || theme.primary || '#ffbb4df4');
    const primaryForeground=argbToCss(theme.primaryForeground || '#ffffffff');
    const secondaryForeground=argbToCss(theme.secondaryForeground || theme.primaryForeground || '#ffffffff');
    const baseBg=dark?'#343434':'#ece9ef';
    const surface=dark?'#3f3f3f':'#ffffff';
    const surfaceAlt=dark?'#292929':'#ddd8e2';
    const textColor=dark?'#f1edf4':'#241f28';
    const mutedColor=dark?'#b7afbd':'#635b69';

    root.style.setProperty('--skua-purple',primary);
    root.style.setProperty('--skua-purple-bright',secondary);
    root.style.setProperty('--skua-purple-light',mixHexColor(secondary,'#ffffff',0.20));
    root.style.setProperty('--skua-purple-dark',mixHexColor(primary,'#000000',0.35));
    root.style.setProperty('--skua-primary-foreground',primaryForeground);
    root.style.setProperty('--skua-secondary-foreground',secondaryForeground);
    root.style.setProperty('--skua-bg',baseBg);
    root.style.setProperty('--skua-bg-2',surface);
    root.style.setProperty('--skua-bg-3',surfaceAlt);
    root.style.setProperty('--skua-panel',surface);
    root.style.setProperty('--skua-text',textColor);
    root.style.setProperty('--skua-muted',mutedColor);
    root.style.setProperty('--skua-border',mixHexColor(primary, dark ? '#777777' : '#888888', 0.58));

    document.body.dataset.skuaBaseTheme=dark?'dark':'light';
  }

  function bindThemes() {
    let data=null, selected=null;
    const colorFields=[['Primary','primary'],['Secondary','secondary'],['Primary Foreground','primaryForeground'],['Secondary Foreground','secondaryForeground']];
    $('theme-colors').innerHTML=colorFields.map(([label,key])=>`<div><label class="skua-label">${label}</label><div class="parity-color-input"><input type="color" id="theme-${key}-picker"><input class="skua-input parity-mono" id="theme-${key}"></div></div>`).join('');
    for(const [,key] of colorFields){const picker=$(`theme-${key}-picker`),text=$(`theme-${key}`);picker.addEventListener('input',()=>text.value=cssToArgb(picker.value));text.addEventListener('change',()=>picker.value=argbToCss(text.value));}

    function fill(theme){
      if(!theme)return;selected=theme;$('theme-name').value=theme.name??'';$('theme-base').value=theme.baseTheme??'Dark';
      for(const [,key] of colorFields){$(`theme-${key}`).value=theme[key]??'';$(`theme-${key}-picker`).value=argbToCss(theme[key]);}
      $('theme-adjust').checked=Boolean(theme.useColorAdjustment);$('theme-ratio').value=theme.desiredContrastRatio??4.5;$('theme-contrast').value=theme.contrast??'Medium';$('theme-selection').value=theme.colorSelection??'All';applyThemeCss(theme);
    }
    function render(){
      const list=$('themes-list');list.textContent='';
      for(const [kind,items] of [['Preset',data?.presets??[]],['User',data?.userThemes??[]]]) for(const theme of items){const row=document.createElement('div');row.className='parity-list-row';if(theme.serialized===data.currentSerialized)row.classList.add('selected');row.innerHTML=`<span class="parity-pill">${kind}</span><span>${esc(theme.name)}</span><span>${esc(theme.baseTheme)}</span>`;row.addEventListener('click',()=>{qsa('.parity-list-row',list).forEach(x=>x.classList.remove('selected'));row.classList.add('selected');fill(theme);});list.appendChild(row);}
      $('theme-background').innerHTML=(data?.backgrounds??[]).map(name=>`<option ${name===data.currentBackground?'selected':''}>${esc(name)}</option>`).join('');
      fill(data?.current);
    }
    async function refresh(){if(!backend())return setStatus('themes-status','Backend offline.');try{data=await cmd('themes.status');render();setStatus('themes-status','Theme settings loaded.',true);}catch(error){setStatus('themes-status',String(error?.message??error));}}
    refreshers['window-themes']=refresh;
    $('themes-apply')?.addEventListener('click',async()=>{if(!selected)return;try{data=await cmd('themes.setCurrent',[selected.serialized??selected.name]);render();toast(`Theme applied: ${data.current?.name??selected.name}`);}catch(error){setStatus('themes-status',String(error?.message??error));}});
    $('theme-save')?.addEventListener('click',async()=>{try{data=await cmd('themes.save',[val('theme-name'),val('theme-base'),val('theme-primary'),val('theme-secondary'),val('theme-primaryForeground'),val('theme-secondaryForeground'),checked('theme-adjust'),Number(val('theme-ratio')),val('theme-contrast'),val('theme-selection')]);render();toast('Theme saved.');}catch(error){setStatus('themes-status',String(error?.message??error));}});
    $('themes-remove')?.addEventListener('click',async()=>{if(!selected)return;try{data=await cmd('themes.remove',[selected.name]);render();toast('User theme removed.');}catch(error){setStatus('themes-status',String(error?.message??error));}});
    function handleBackgroundApplyResult(result,{imported=false}={}){
      data=result;render();
      if(result?.backgroundReloadSuggested){
        setStatus('themes-status','Background saved. Reloading the AQW client to apply it...',true);
        toast('Background saved. Reloading AQW...');
        setTimeout(()=>window.location.reload(),350);
        return;
      }
      if(result?.backgroundApplyMode==='reload-required'){
        setStatus('themes-status','Background saved. It will apply on the next game load; the active session was kept running.',true);
        toast('Background saved for the next game load.');
        return;
      }
      setStatus('themes-status',imported?'Background imported and applied.':'Game background applied.',true);
      toast(imported?'Background imported and applied.':'Game background applied.');
    }
    $('theme-background-apply')?.addEventListener('click',async()=>{try{handleBackgroundApplyResult(await cmd('themes.background.set',[val('theme-background')],30000));}catch(error){setStatus('themes-status',String(error?.message??error));}});
    $('theme-background-import')?.addEventListener('click',async()=>{try{const pick=await shell('dialog.open-file',['Import Skua Background',data?.themesDirectory??'', ['swf'],false]);if(!pick?.path)return;handleBackgroundApplyResult(await cmd('themes.background.import',[pick.path],30000),{imported:true});}catch(error){setStatus('themes-status',String(error?.message??error));}});
    $('theme-background-folder')?.addEventListener('click',()=>void (data?.themesDirectory?shell('shell.open-path',[data.themesDirectory]):Promise.resolve()).catch(e=>setStatus('themes-status',String(e?.message??e))));
    $('theme-background-repo')?.addEventListener('click',()=>void shell('shell.open-external',[data?.backgroundRepository??'https://github.com/auqw/SkuaBackgrounds']).catch(e=>setStatus('themes-status',String(e?.message??e))));
  }

  function normalizeGesture(event) {
    if (event.key === 'Backspace' || event.key === 'Delete') return '';
    const ignored=new Set(['Control','Shift','Alt','Meta']); if(ignored.has(event.key))return null;
    const parts=[];if(event.ctrlKey)parts.push('Ctrl');if(event.altKey)parts.push('Alt');if(event.shiftKey)parts.push('Shift');if(event.metaKey)parts.push('Meta');
    let key=event.key.length===1?event.key.toUpperCase():event.key.replace(/^Arrow/,'');
    parts.push(key);return parts.join('+');
  }

  function bindHotKeys() {
    const labels={ToggleScript:'Toggle Script',LoadScript:'Load Script',OpenBank:'Open Bank',OpenConsole:'Open Console',ToggleAutoAttack:'Toggle Auto Attack',ToggleAutoHunt:'Toggle Auto Hunt',ToggleLagKiller:'Toggle Lag Killer'};
    async function refresh(){if(!backend())return setStatus('hotkeys-status','Backend offline.');try{const data=await cmd('hotkeys.status');state.hotKeys={};const host=$('hotkeys-body');host.textContent='';for(const binding of data.bindings??[]){state.hotKeys[binding.name]=binding.gesture??'';const row=document.createElement('div');row.className='parity-option-row';row.innerHTML=`<div class="parity-option-label">${esc(labels[binding.name]??human(binding.name))}</div>`;const input=document.createElement('input');input.className='skua-input parity-mono';input.readOnly=true;input.value=binding.gesture??'';input.dataset.hotkey=binding.name;input.addEventListener('keydown',(event)=>{event.preventDefault();event.stopPropagation();const gesture=normalizeGesture(event);if(gesture===null)return;input.value=gesture;state.hotKeys[binding.name]=gesture;});row.appendChild(input);host.appendChild(row);}setStatus('hotkeys-status','Bindings loaded.',true);}catch(error){setStatus('hotkeys-status',String(error?.message??error));}}
    refreshers['window-hotkeys']=refresh;
    $('hotkeys-save')?.addEventListener('click',async()=>{try{const data=await cmd('hotkeys.save',[state.hotKeys]);state.hotKeys=Object.fromEntries((data.bindings??[]).map(x=>[x.name,x.gesture]));toast('HotKeys saved.');setStatus('hotkeys-status','Saved.',true);}catch(error){setStatus('hotkeys-status',String(error?.message??error));}});
  }

  function bindRuntime() {
    let dropsData=null,questsData=null,boostData=null;
    qsa('[data-runtime-tab]').forEach(btn=>btn.addEventListener('click',()=>{
      qsa('[data-runtime-tab]').forEach(x=>x.classList.toggle('active',x===btn));
      for(const name of ['drops','quests','boosts']) $(`runtime-${name}-panel`).hidden=btn.dataset.runtimeTab!==name;
    }));

    function renderDrops(){
      const items=dropsData?.items??[];
      $('runtime-drops-panel').innerHTML=`<div class="parity-toolbar"><input class="skua-input parity-fill" id="runtime-drop-input" placeholder="Name | 1234"><button class="skua-button" id="runtime-drop-add">Add</button><button class="skua-button secondary" id="runtime-drop-remove">Remove Selected</button><button class="skua-button secondary" id="runtime-drop-clear">Clear</button></div><div class="parity-list" id="runtime-drop-list">${items.map(item=>`<label class="parity-list-row"><input type="checkbox" data-runtime-drop="${esc(item)}"><span>${esc(item)}</span><span></span></label>`).join('')}</div><div class="parity-grid three"><div><label class="skua-label">Interval</label><input class="skua-input" id="runtime-drop-interval" type="number" min="0" value="${esc(dropsData?.interval??0)}"></div><label><input id="runtime-drop-reject" type="checkbox" ${dropsData?.rejectElse?'checked':''}> Reject Else</label><label><input id="runtime-drop-ac" type="checkbox" ${dropsData?.acceptACDrops?'checked':''}> Accept AC Drops</label></div><div class="parity-actions"><button class="skua-button" id="runtime-drop-toggle">${dropsData?.enabled?'Stop':'Start'}</button><button class="skua-button secondary" id="runtime-drop-config">Apply</button></div>`;
      $('runtime-drop-add').onclick=()=>void cmd('runtime.drops.add',[val('runtime-drop-input')]).then(d=>{dropsData=d;renderDrops();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
      $('runtime-drop-remove').onclick=()=>{const selected=qsa('[data-runtime-drop]:checked').map(x=>x.dataset.runtimeDrop);void cmd('runtime.drops.remove',[selected]).then(d=>{dropsData=d;renderDrops();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));};
      $('runtime-drop-clear').onclick=()=>void cmd('runtime.drops.clear').then(d=>{dropsData=d;renderDrops();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
      $('runtime-drop-toggle').onclick=()=>void cmd('runtime.drops.toggle',[],30000).then(d=>{dropsData=d;renderDrops();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
      $('runtime-drop-config').onclick=()=>void cmd('runtime.drops.configure',[Number(val('runtime-drop-interval')),checked('runtime-drop-reject'),checked('runtime-drop-ac')]).then(d=>{dropsData=d;renderDrops();toast('Drop settings applied.');}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
    }
    function renderQuests(){
      $('runtime-quests-panel').innerHTML=`<div class="parity-grid"><div><label class="skua-label">Quest ID(s)</label><input class="skua-input" id="runtime-quest-input" placeholder="1234 | 5678"></div><div><label class="skua-label">Reward ID (-1 = any)</label><input class="skua-input" id="runtime-reward-id" type="number" value="-1"></div></div><div class="parity-actions"><button class="skua-button" id="runtime-quest-add">Register</button><button class="skua-button secondary" id="runtime-quest-remove">Unregister Selected</button><button class="skua-button secondary" id="runtime-quest-clear">Unregister All</button></div><div class="parity-list" id="runtime-quest-list">${(questsData?.quests??[]).map(q=>`<label class="parity-list-row"><input type="checkbox" data-runtime-quest="${q.questId}"><span>Quest ${q.questId}</span><span>Reward ${q.rewardId}</span></label>`).join('')}</div>`;
      $('runtime-quest-add').onclick=()=>void cmd('runtime.quests.add',[val('runtime-quest-input'),Number(val('runtime-reward-id'))]).then(d=>{questsData=d;renderQuests();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
      $('runtime-quest-remove').onclick=()=>void cmd('runtime.quests.remove',[qsa('[data-runtime-quest]:checked').map(x=>Number(x.dataset.runtimeQuest))]).then(d=>{questsData=d;renderQuests();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
      $('runtime-quest-clear').onclick=()=>void cmd('runtime.quests.clear').then(d=>{questsData=d;renderQuests();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
    }
    function renderBoosts(){
      const defs=[['Class','classBoostID','useClassBoost'],['Experience','experienceBoostID','useExperienceBoost'],['Gold','goldBoostID','useGoldBoost'],['Reputation','reputationBoostID','useReputationBoost']];
      $('runtime-boosts-panel').innerHTML=`<div class="parity-card">${defs.map(([label,idKey,useKey])=>`<div class="parity-option-row"><div><div class="parity-option-label">${label} Boost</div></div><div class="parity-row"><input data-boost-use="${useKey}" type="checkbox" ${boostData?.[useKey]?'checked':''}><input class="skua-input" data-boost-id="${idKey}" type="number" value="${boostData?.[idKey]??0}"></div></div>`).join('')}</div><div class="parity-actions"><button class="skua-button" id="boost-toggle">${boostData?.enabled?'Stop':'Start'}</button><button class="skua-button secondary" id="boost-detect-inv">Set IDs (Inventory)</button><button class="skua-button secondary" id="boost-detect-bank">Set IDs (Inventory + Bank)</button></div>`;
      qsa('[data-boost-use]').forEach(el=>el.addEventListener('change',()=>void cmd('runtime.boosts.set',[el.dataset.boostUse,el.checked]).then(d=>{boostData=d;renderBoosts();}).catch(e=>setStatus('runtime-status',String(e?.message??e)))));
      qsa('[data-boost-id]').forEach(el=>el.addEventListener('change',()=>void cmd('runtime.boosts.set',[el.dataset.boostId,Number(el.value)]).then(d=>{boostData=d;renderBoosts();}).catch(e=>setStatus('runtime-status',String(e?.message??e)))));
      $('boost-toggle').onclick=()=>void cmd('runtime.boosts.toggle',[],30000).then(d=>{boostData=d;renderBoosts();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
      $('boost-detect-inv').onclick=()=>void cmd('runtime.boosts.detect',[false]).then(d=>{boostData=d;renderBoosts();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
      $('boost-detect-bank').onclick=()=>void cmd('runtime.boosts.detect',[true],30000).then(d=>{boostData=d;renderBoosts();}).catch(e=>setStatus('runtime-status',String(e?.message??e)));
    }
    async function refresh(){if(!backend())return setStatus('runtime-status','Backend offline.');try{[dropsData,questsData,boostData]=await Promise.all([cmd('runtime.drops.status'),cmd('runtime.quests.status'),cmd('runtime.boosts.status')]);renderDrops();renderQuests();renderBoosts();setStatus('runtime-status','Runtime helpers loaded.',true);}catch(error){setStatus('runtime-status',String(error?.message??error));}}
    refreshers['window-runtime']=refresh;
  }

  function bindNotify() {
    let data=null;
    function render(){
      $('notify-list').innerHTML=(data?.items??[]).map(item=>`<label class="parity-list-row"><input type="checkbox" data-notify-name="${esc(item)}"><span>${esc(item)}</span><span></span></label>`).join('');
      $('notify-count').value=data?.soundCount??5;$('notify-delay').value=data?.soundDelay??200;setStatus('notify-status',`${data?.items?.length??0} tracked item(s) · ${data?.pending??0} pending alert(s).`,true);
    }
    async function refresh(){if(!backend())return setStatus('notify-status','Backend offline.');try{data=await cmd('notify.status');render();}catch(error){setStatus('notify-status',String(error?.message??error));}}
    refreshers['window-notify-drop']=refresh;
    $('notify-add')?.addEventListener('click',()=>void cmd('notify.add',[val('notify-input')]).then(d=>{data=d;$('notify-input').value='';render();}).catch(e=>setStatus('notify-status',String(e?.message??e))));
    $('notify-remove')?.addEventListener('click',()=>void cmd('notify.remove',[qsa('[data-notify-name]:checked').map(x=>x.dataset.notifyName)]).then(d=>{data=d;render();}).catch(e=>setStatus('notify-status',String(e?.message??e))));
    $('notify-clear')?.addEventListener('click',()=>void cmd('notify.clear').then(d=>{data=d;render();}).catch(e=>setStatus('notify-status',String(e?.message??e))));
    const configure=()=>void cmd('notify.configure',[Number(val('notify-count')),Number(val('notify-delay'))]).then(d=>{data=d;render();}).catch(e=>setStatus('notify-status',String(e?.message??e)));
    $('notify-count')?.addEventListener('change',configure);$('notify-delay')?.addEventListener('change',configure);
    $('notify-test')?.addEventListener('click',async()=>{try{const result=await cmd('notify.test');await shell('shell.beep',[result.soundCount,result.soundDelay],30000);toast('Notify Drop sound test completed.');}catch(error){setStatus('notify-status',String(error?.message??error));}});
  }

  function startNotifyPoll(){
    clearInterval(state.notifyTimer);
    state.notifyTimer=setInterval(async()=>{
      if(!backend())return;
      try{const data=await cmd('notify.poll',[],4000);for(const item of data.events??[]){toast(`Drop: ${item.name} (ID ${item.itemID})`,'good');void shell('shell.beep',[item.soundCount,item.soundDelay],30000).catch(()=>{});}}
      catch{/* backend may be restarting */}
    },2000);
  }

  function bindFastTravel() {
    let data=null;
    function render(){
      const query=val('fast-search').trim().toLowerCase();const items=(data?.items??[]).filter(x=>!query||`${x.descriptionName} ${x.mapName} ${x.cell} ${x.pad}`.toLowerCase().includes(query));
      $('fast-list').textContent='';for(const item of items){const row=document.createElement('div');row.className='parity-list-row';if(item.index===state.selectedTravelIndex)row.classList.add('selected');row.innerHTML=`<span>${item.index+1}</span><span><strong>${esc(item.descriptionName)}</strong><br><span class="parity-option-desc">${esc(item.mapName)} · ${esc(item.cell)} · ${esc(item.pad)}</span></span><span></span>`;row.addEventListener('click',()=>{state.selectedTravelIndex=item.index;$('fast-name').value=item.descriptionName;$('fast-map').value=item.mapName;$('fast-cell').value=item.cell;$('fast-pad').value=item.pad;render();});$('fast-list').appendChild(row);}
      $('fast-private-enabled').checked=Boolean(data?.usePrivateRoom);$('fast-private').value=data?.privateRoomNumber??0;
    }
    async function refresh(){if(!backend())return setStatus('fast-status','Backend offline.');try{data=await cmd('travel.list');render();setStatus('fast-status',`${data.items?.length??0} saved destination(s).`,true);}catch(error){setStatus('fast-status',String(error?.message??error));}}
    refreshers['window-fast-travel']=refresh;
    $('fast-search')?.addEventListener('input',render);
    $('fast-save')?.addEventListener('click',async()=>{try{data=await cmd('travel.add',[val('fast-name'),val('fast-map'),val('fast-cell'),val('fast-pad')]);render();toast('Fast Travel destination added.');}catch(error){setStatus('fast-status',String(error?.message??error));}});
    $('fast-update')?.addEventListener('click',async()=>{if(state.selectedTravelIndex<0)return;try{data=await cmd('travel.update',[state.selectedTravelIndex,val('fast-name'),val('fast-map'),val('fast-cell'),val('fast-pad')]);render();toast('Destination updated.');}catch(error){setStatus('fast-status',String(error?.message??error));}});
    $('fast-remove')?.addEventListener('click',async()=>{if(state.selectedTravelIndex<0)return;try{data=await cmd('travel.remove',[state.selectedTravelIndex]);state.selectedTravelIndex=-1;render();}catch(error){setStatus('fast-status',String(error?.message??error));}});
    $('fast-clear')?.addEventListener('click',async()=>{try{data=await cmd('travel.clear');state.selectedTravelIndex=-1;render();}catch(error){setStatus('fast-status',String(error?.message??error));}});
    $('fast-current')?.addEventListener('click',async()=>{try{const current=await cmd('travel.current');$('fast-map').value=current.mapName??'';$('fast-cell').value=current.cell??'Enter';$('fast-pad').value=current.pad??'Spawn';}catch(error){setStatus('fast-status',String(error?.message??error));}});
    $('fast-go')?.addEventListener('click',async()=>{if(state.selectedTravelIndex<0)return setStatus('fast-status','Select a saved destination first.');try{await cmd('travel.settings',[checked('fast-private-enabled'),Number(val('fast-private'))]);await cmd('travel.go',[state.selectedTravelIndex]);setStatus('fast-status','Travel requested. Map change may take a moment.',true);}catch(error){setStatus('fast-status',String(error?.message??error));}});
    const setting=()=>void (backend()?cmd('travel.settings',[checked('fast-private-enabled'),Number(val('fast-private'))]):Promise.resolve()).then(d=>{if(d)data=d;}).catch(e=>setStatus('fast-status',String(e?.message??e)));
    $('fast-private-enabled')?.addEventListener('change',setting);$('fast-private')?.addEventListener('change',setting);
  }

  function bindCurrentDrops() {
    let data=null;
    function render(){const list=$('current-drops-list');list.innerHTML=(data?.drops??[]).map(item=>`<label class="parity-list-row"><input type="checkbox" data-drop-id="${item.id}"><span><strong>${esc(item.name)}</strong><br><span class="parity-option-desc">${esc(item.category)} · Qty ${item.quantity}</span></span><span>${item.coins?'AC':''}</span></label>`).join('');setStatus('current-drops-status',`${data?.drops?.length??0} current drop(s).`,true);}
    async function refresh(){if(!backend())return;try{data=await cmd('drops.current');render();}catch(error){setStatus('current-drops-status',String(error?.message??error));}}
    refreshers['window-current-drops']=refresh;
    $('current-drops-refresh')?.addEventListener('click',()=>void refresh());
    $('current-drops-pick')?.addEventListener('click',()=>void cmd('drops.pick',[qsa('[data-drop-id]:checked').map(x=>Number(x.dataset.dropId))]).then(d=>{data=d;render();}).catch(e=>setStatus('current-drops-status',String(e?.message??e))));
    $('current-drops-all')?.addEventListener('click',()=>void cmd('drops.pickAll').then(refresh).catch(e=>setStatus('current-drops-status',String(e?.message??e))));
    $('current-drops-ac')?.addEventListener('click',()=>void cmd('drops.pickAC').then(refresh).catch(e=>setStatus('current-drops-status',String(e?.message??e))));
  }

  function bindLoader() {
    let data={quests:[]};
    function filtered(){const query=val('loader-search').trim().toLowerCase();return (data.quests??[]).filter(q=>!query||`${q.id} ${q.name}`.toLowerCase().includes(query));}
    function render(){
      $('loader-quest-list').innerHTML=filtered().map(q=>`<label class="parity-list-row"><input type="checkbox" data-loader-quest="${q.id}"><span>${esc(q.name)}</span><span>${q.id}</span></label>`).join('');
      setStatus('loader-status',`${data.isLoading?'Loading · ':''}${data.progress??''}${data.quests?.length?` · ${data.quests.length} quests`:''}`,!data.isLoading);
    }
    async function refresh(){if(!backend())return setStatus('loader-status','Backend offline.');try{data=await cmd('loader.status');render();}catch(error){setStatus('loader-status',String(error?.message??error));}}
    refreshers['window-loader']=refresh;
    $('loader-search')?.addEventListener('input',render);
    $('loader-load')?.addEventListener('click',()=>void cmd('loader.load',[Number(val('loader-type')),val('loader-ids')],30000).then(()=>toast('Loader command sent.')).catch(e=>setStatus('loader-status',String(e?.message??e))));
    $('loader-quest-file')?.addEventListener('click',()=>void cmd('loader.quests.get',[],30000).then(d=>{data=d;render();}).catch(e=>setStatus('loader-status',String(e?.message??e))));
    $('loader-update')?.addEventListener('click',()=>void cmd('loader.quests.update',[false]).then(d=>{data=d;render();startLoaderPoll();}).catch(e=>setStatus('loader-status',String(e?.message??e))));
    $('loader-update-all')?.addEventListener('click',()=>void cmd('loader.quests.update',[true]).then(d=>{data=d;render();startLoaderPoll();}).catch(e=>setStatus('loader-status',String(e?.message??e))));
    $('loader-range')?.addEventListener('click',()=>{const start=Number(prompt('Start Quest ID','1'));const end=Number(prompt('End Quest ID',String(start+100)));if(!Number.isFinite(start)||!Number.isFinite(end))return;void cmd('loader.quests.range',[start,end]).then(d=>{data=d;render();startLoaderPoll();}).catch(e=>setStatus('loader-status',String(e?.message??e)));});
    $('loader-cancel')?.addEventListener('click',()=>void cmd('loader.quests.cancel').then(d=>{data=d;render();}).catch(e=>setStatus('loader-status',String(e?.message??e))));
    $('loader-selected-load')?.addEventListener('click',()=>{const ids=qsa('[data-loader-quest]:checked').map(x=>Number(x.dataset.loaderQuest));if(!ids.length)return;void cmd('loader.load',[1,ids.join(',')],30000).then(()=>toast('Quest(s) loaded.')).catch(e=>setStatus('loader-status',String(e?.message??e)));});
    $('loader-fake')?.addEventListener('click',()=>{const ids=qsa('[data-loader-quest]:checked').map(x=>Number(x.dataset.loaderQuest));if(ids.length!==1)return setStatus('loader-status','Select exactly one quest.');void cmd('loader.quests.fakeComplete',[ids[0]]).then(r=>setStatus('loader-status',`Fake Complete ${ids[0]}: ${r.completed}`,true)).catch(e=>setStatus('loader-status',String(e?.message??e)));});
    async function copyMode(mode){const rows=filtered();let text='';if(mode==='ids')text=rows.map(q=>q.id).join('\n');if(mode==='names')text=rows.map(q=>q.name).join('\n');if(mode==='both')text=rows.map(q=>`${q.id} - ${q.name}`).join('\n');try{await shell('clipboard.write-text',[text]);toast('Quest data copied.');}catch(error){setStatus('loader-status',String(error?.message??error));}}
    $('loader-copy-ids')?.addEventListener('click',()=>void copyMode('ids'));$('loader-copy-names')?.addEventListener('click',()=>void copyMode('names'));$('loader-copy-both')?.addEventListener('click',()=>void copyMode('both'));
    function startLoaderPoll(){clearInterval(state.loaderTimer);state.loaderTimer=setInterval(async()=>{if(!$('window-loader')?.classList.contains('open'))return;try{data=await cmd('loader.status');render();if(!data.isLoading){clearInterval(state.loaderTimer);state.loaderTimer=null;}}catch{}},700);}
  }

  function bindGrabber() {
    let data=null;
    const actions={
      'Shop Items':['Buy'], 'Shop IDs':['Load Shop'], 'Quests':['Open','Accept','Register','Fake Complete','Unregister All'],
      'Inventory Items':['Equip','Sell','Sell All','To Bank'], 'House Inventory Items':['To Bank'], 'Temp Inventory Items':[], 'Bank Items':['To Inventory'],
      'Cell Monsters':['Kill'], 'Map Monsters':['Kill','Teleport To'], 'GetMap Item IDs':['Open','Accept','Fake Complete','Get Map Item']
    };
    function render(){
      const query=val('grabber-search').trim().toLowerCase();const list=(data?.items??[]).filter(x=>!query||JSON.stringify(x).toLowerCase().includes(query));
      $('grabber-list').innerHTML=list.map(item=>`<label class="parity-list-row"><input type="checkbox" data-grabber-index="${item.index}"><span><strong>${esc(item.name??item.kind)}</strong><br><span class="parity-option-desc">${esc(item.kind)} · ID ${item.id??''}${item.cell?` · ${esc(item.cell)}`:''}${item.cost!==undefined?` · Cost ${item.cost}`:''}</span></span><span>${item.quantity??''}</span></label>`).join('');
      const type=val('grabber-type');$('grabber-actions').innerHTML=(actions[type]??[]).map(action=>`<button class="skua-button ${action==='Unregister All'?'secondary':''}" type="button" data-grabber-action="${esc(action)}">${esc(action)}</button>`).join('');
      qsa('[data-grabber-action]').forEach(btn=>btn.addEventListener('click',()=>void runAction(btn.dataset.grabberAction)));
      setStatus('grabber-status',`${data?.items?.length??0} item(s).`,true);
    }
    async function refresh(){if(!backend())return setStatus('grabber-status','Backend offline.');try{data=await cmd('grabber.status',[val('grabber-type')],30000);render();}catch(error){data={items:[]};render();setStatus('grabber-status',String(error?.message??error));}}
    async function runAction(action){const indices=qsa('[data-grabber-index]:checked').map(x=>Number(x.dataset.grabberIndex));let quantity=1;if(['Buy','Sell','Get Map Item'].includes(action)){const q=prompt('Quantity','1');if(q===null)return;quantity=Math.max(1,Number(q)||1);}try{const result=await cmd('grabber.action',[val('grabber-type'),action,indices,quantity],120000);setStatus('grabber-status',result.message??'Finished.',true);await refresh();}catch(error){setStatus('grabber-status',String(error?.message??error));}}
    refreshers['window-grabber']=refresh;
    $('grabber-refresh')?.addEventListener('click',()=>void refresh());$('grabber-type')?.addEventListener('change',()=>void refresh());$('grabber-search')?.addEventListener('input',render);
  }

  function bindJunk() {
    let data=null;
    function render(){const query=val('junk-search').trim().toLowerCase();const items=(data?.items??[]).filter(i=>!query||`${i.name} ${i.id}`.toLowerCase().includes(query));$('junk-list').innerHTML=items.map(item=>`<label class="parity-list-row"><input type="checkbox" data-junk-id="${item.id}" ${item.junk?'checked':''}><span>${esc(item.name)}</span><span>${item.inBank?'Bank':'Inv'}</span></label>`).join('');qsa('[data-junk-id]').forEach(box=>box.addEventListener('change',()=>void cmd('junk.set',[Number(box.dataset.junkId),box.checked]).then(d=>{data=d;render();}).catch(e=>setStatus('junk-status',String(e?.message??e)))));$('junk-skip-warning').checked=Boolean(data?.skipSellWarning);setStatus('junk-status',`${data?.items?.filter(x=>x.junk).length??0} junk item(s).`,true);}
    async function refresh(){if(!backend())return setStatus('junk-status','Backend offline.');try{data=await cmd('junk.status',[],30000);render();}catch(error){setStatus('junk-status',String(error?.message??error));}}
    refreshers['window-junk']=refresh;$('junk-search')?.addEventListener('input',render);$('junk-refresh')?.addEventListener('click',()=>void refresh());
    $('junk-clear')?.addEventListener('click',()=>void cmd('junk.clear').then(d=>{data=d;render();}).catch(e=>setStatus('junk-status',String(e?.message??e))));
    $('junk-skip-warning')?.addEventListener('change',()=>void cmd('junk.warning',[checked('junk-skip-warning')]).then(d=>{data=d;render();}).catch(e=>setStatus('junk-status',String(e?.message??e))));
    $('junk-sell')?.addEventListener('click',async()=>{if(!data?.skipSellWarning&&!confirm('Sell all items currently marked as junk?'))return;try{await cmd('junk.sellAll',[],120000);toast('Sell All Junk started.');}catch(error){setStatus('junk-status',String(error?.message??error));}});
  }

  function bindStats() {
    function render(data){const entries=[['Kills',data.kills],['Deaths',data.deaths],['Quests Accepted',data.questsAccepted],['Quests Completed',data.questsCompleted],['Drops',data.drops],['Relogins',data.relogins],['Time',data.time],['Inventory',`${data.inventory?.used??0}/${data.inventory?.max??0} (${data.inventory?.free??0} free)`],['Bank',`${data.bank?.used??0}/${data.bank?.max??0} (${data.bank?.free??0} free)`]];$('stats-grid').innerHTML=entries.map(([label,value])=>`<div class="parity-card"><div class="parity-option-desc">${esc(label)}</div><div style="font-size:18px;font-weight:700">${esc(value??0)}</div></div>`).join('');setStatus('stats-status','Statistics loaded.',true);}
    async function refresh(){if(!backend())return;try{render(await cmd('stats.status'));}catch(error){setStatus('stats-status',String(error?.message??error));}}
    refreshers['window-stats']=refresh;$('stats-refresh')?.addEventListener('click',()=>void refresh());$('stats-reset')?.addEventListener('click',()=>void cmd('stats.reset').then(refresh).catch(e=>setStatus('stats-status',String(e?.message??e))));$('stats-space')?.addEventListener('click',()=>void cmd('stats.getSpace',[],30000).then(refresh).catch(e=>setStatus('stats-status',String(e?.message??e))));
  }

  function bindConsole() {
    $('console-run')?.addEventListener('click',async()=>{if(!backend())return setStatus('console-status','Backend offline.');setStatus('console-status','Compiling / running…');try{await cmd('console.run',[val('console-code')],60000);setStatus('console-status','Executed.',true);}catch(error){setStatus('console-status',String(error?.message??error));}});
  }

  function bindAuto() {
    let data=null;
    function fillModes(){const className=val('auto-class');let modes=data?.modes?.[className]??['Base'];if(!modes.length)modes=['Base'];const current=val('auto-class-mode');$('auto-class-mode').innerHTML=modes.map(m=>`<option ${m===current?'selected':''}>${esc(m)}</option>`).join('');}
    function render(){const current=val('auto-class')||data?.currentClass||'';$('auto-class').innerHTML=['',...(data?.classes??[])].map(name=>`<option ${name===current?'selected':''}>${esc(name||'[Current / no override]')}</option>`).join('');fillModes();switchState('auto-attack-switch',Boolean(data?.running)&&state.autoMode==='attack');switchState('auto-hunt-switch',Boolean(data?.running)&&state.autoMode==='hunt');setStatus('auto-status',data?.running?`Running ${state.autoMode}.`:'Stopped.',true);}
    async function refresh(){if(!backend())return setStatus('auto-status','Backend offline.');try{data=await cmd('auto.status');render();}catch(error){setStatus('auto-status',String(error?.message??error));}}
    refreshers['window-auto']=refresh;
    $('auto-class')?.addEventListener('change',async()=>{fillModes();const className=val('auto-class');if(className){try{data=await cmd('auto.equip',[className],30000);render();}catch(error){setStatus('auto-status',String(error?.message??error));}}});
    async function toggle(mode){try{if(data?.running){await cmd('auto.stop',[],30000);data=await cmd('auto.status');render();return;}state.autoMode=mode;await cmd(mode==='hunt'?'auto.startHunt':'auto.startAttack',[val('auto-class')||null,val('auto-class-mode')||'Base',val('auto-manual-ids')],30000);data=await cmd('auto.status');render();}catch(error){setStatus('auto-status',String(error?.message??error));}}
    $('auto-attack-switch')?.addEventListener('click',()=>void toggle('attack'));$('auto-hunt-switch')?.addEventListener('click',()=>void toggle('hunt'));
  }

  function bindSkills() {
    let data = null;
    let currentTokens = [];
    let currentDisplays = [];
    let selectedCurrent = -1;
    let editingCurrent = -1;

    function prop(skill, name) {
      return skill?.[name] ?? skill?.[name[0].toLowerCase() + name.slice(1)];
    }

    function syncDsl() {
      $('skills-dsl').value = currentTokens.join(' | ');
    }

    function setUseMode(waitForCooldown) {
      $('skills-wait-mode').checked = Boolean(waitForCooldown);
      $('skills-available-mode').checked = !Boolean(waitForCooldown);
    }

    function currentUseMode() {
      return checked('skills-wait-mode') ? 'WaitForCooldown' : 'UseIfAvailable';
    }

    function renderCurrentSkills() {
      const host = $('skills-current-list');
      host.textContent = '';
      currentTokens.forEach((token, index) => {
        const row = document.createElement('div');
        row.className = 'parity-list-row';
        if (index === selectedCurrent) row.classList.add('selected');
        row.innerHTML = `<span>${index + 1}</span><span>${esc(currentDisplays[index] || token)}</span><span>${index === editingCurrent ? '✎' : ''}</span>`;
        row.addEventListener('click', () => {
          selectedCurrent = index;
          renderCurrentSkills();
          host.focus();
        });
        row.addEventListener('dblclick', () => void editCurrent());
        host.appendChild(row);
      });
      syncDsl();
    }

    async function hydrateTokens(skillsText) {
      currentTokens = String(skillsText ?? '')
        .split('|')
        .map((token) => token.trim())
        .filter(Boolean);
      currentDisplays = [...currentTokens];
      selectedCurrent = currentTokens.length ? 0 : -1;
      editingCurrent = -1;

      // DisplayString in the Windows client comes from SkillItemViewModel.
      // Ask Skua.Core for that same representation instead of reimplementing it.
      await Promise.all(currentTokens.map(async (token, index) => {
        try {
          const parsed = await cmd('skills.parse', [token]);
          currentDisplays[index] = parsed?.display ?? token;
          currentTokens[index] = parsed?.converted ?? token;
        } catch {
          currentDisplays[index] = token;
        }
      }));
      renderCurrentSkills();
    }

    function renderSaved() {
      const query = val('skills-search').trim().toLowerCase();
      const skills = (data?.skills ?? []).filter((skill) => {
        const text = `${prop(skill, 'ClassName')} ${prop(skill, 'ClassUseMode')} ${prop(skill, 'Skills')}`.toLowerCase();
        return !query || text.includes(query);
      });

      const host = $('skills-list');
      host.textContent = '';
      for (const skill of skills) {
        const row = document.createElement('div');
        row.className = 'parity-list-row';
        if (state.selectedSkill === skill) row.classList.add('selected');
        row.innerHTML = `<span>${esc(prop(skill, 'ClassUseMode') ?? 'Base')}</span><span><strong>${esc(prop(skill, 'ClassName'))}</strong><br><span class="parity-option-desc">${esc(prop(skill, 'Skills'))}</span></span><span></span>`;
        row.addEventListener('click', async () => {
          state.selectedSkill = skill;
          $('skills-class').value = prop(skill, 'ClassName') ?? '';
          $('skills-class-mode').value = prop(skill, 'ClassUseMode') ?? 'Base';
          $('skills-timeout').value = prop(skill, 'SkillTimeout') ?? 100;
          $('skills-reset-target').checked = Boolean(prop(skill, 'ResetComboOnTargetChange'));
          setUseMode((prop(skill, 'SkillUseMode') ?? 'UseIfAvailable') !== 'UseIfAvailable');
          await hydrateTokens(prop(skill, 'Skills') ?? '');
          renderSaved();
        });
        host.appendChild(row);
      }

      const currentClassMode = val('skills-class-mode');
      $('skills-class-mode').innerHTML = (data?.classUseModes ?? [])
        .map((mode) => `<option ${mode === currentClassMode ? 'selected' : ''}>${esc(mode)}</option>`)
        .join('');
      setStatus('skills-status', `${data?.skills?.length ?? 0} saved skill set(s).`, true);
    }

    async function refresh() {
      if (!backend()) return setStatus('skills-status', 'Backend offline.');
      try {
        data = await cmd('skills.status', [], 30000);
        renderSaved();
      } catch (error) {
        setStatus('skills-status', String(error?.message ?? error));
      }
    }

    refreshers['window-skills'] = refresh;
    $('skills-search')?.addEventListener('input', renderSaved);
    $('skills-reload')?.addEventListener('click', () => void cmd('skills.reload', [], 30000).then(() => refresh()).catch((e) => setStatus('skills-status', String(e?.message ?? e))));

    $('skills-wait-mode')?.addEventListener('change', () => setUseMode(checked('skills-wait-mode')));
    $('skills-available-mode')?.addEventListener('change', () => setUseMode(!checked('skills-available-mode')));

    function selectDelta(delta) {
      if (!currentTokens.length) {
        selectedCurrent = -1;
        renderCurrentSkills();
        return;
      }
      if (selectedCurrent < 0) selectedCurrent = 0;
      else selectedCurrent = Math.max(0, Math.min(currentTokens.length - 1, selectedCurrent + delta));
      renderCurrentSkills();
    }

    function moveDelta(delta) {
      if (selectedCurrent < 0 || selectedCurrent >= currentTokens.length) {
        if (currentTokens.length) selectedCurrent = 0;
        renderCurrentSkills();
        return;
      }
      const next = selectedCurrent + delta;
      if (next < 0 || next >= currentTokens.length) return;
      [currentTokens[selectedCurrent], currentTokens[next]] = [currentTokens[next], currentTokens[selectedCurrent]];
      [currentDisplays[selectedCurrent], currentDisplays[next]] = [currentDisplays[next], currentDisplays[selectedCurrent]];
      if (editingCurrent === selectedCurrent) editingCurrent = next;
      else if (editingCurrent === next) editingCurrent = selectedCurrent;
      selectedCurrent = next;
      renderCurrentSkills();
    }

    function removeCurrent() {
      if (selectedCurrent < 0 || selectedCurrent >= currentTokens.length) return;
      currentTokens.splice(selectedCurrent, 1);
      currentDisplays.splice(selectedCurrent, 1);
      if (editingCurrent === selectedCurrent) editingCurrent = -1;
      else if (editingCurrent > selectedCurrent) editingCurrent--;
      selectedCurrent = Math.min(selectedCurrent - 1, currentTokens.length - 1);
      renderCurrentSkills();
    }

    async function parseIntoRuleEditor(text) {
      const result = await cmd('skills.parse', [text]);
      const r = result.rules ?? {};
      $('rule-skill').value = result.skill ?? 1;
      $('rule-use').checked = Boolean(r.UseRuleBool ?? r.useRuleBool);
      $('rule-wait').value = r.WaitUseValue ?? r.waitUseValue ?? 0;
      $('rule-hp-greater').checked = Boolean(r.HealthGreaterThanBool ?? r.healthGreaterThanBool);
      $('rule-hp').value = r.HealthUseValue ?? r.healthUseValue ?? 0;
      $('rule-hp-percent').checked = Boolean(r.HealthIsPercentage ?? r.healthIsPercentage);
      $('rule-mp-greater').checked = Boolean(r.ManaGreaterThanBool ?? r.manaGreaterThanBool);
      $('rule-mp').value = r.ManaUseValue ?? r.manaUseValue ?? 0;
      $('rule-mp-percent').checked = Boolean(r.ManaIsPercentage ?? r.manaIsPercentage);
      $('rule-aura-greater').checked = Boolean(r.AuraGreaterThanBool ?? r.auraGreaterThanBool);
      $('rule-aura-value').value = r.AuraUseValue ?? r.auraUseValue ?? 0;
      $('rule-aura-target').value = r.AuraTargetIndex ?? r.auraTargetIndex ?? 0;
      $('rule-aura').value = r.AuraName ?? r.auraName ?? '';
      $('rule-skip').checked = Boolean(r.SkipUseBool ?? r.skipUseBool);
      $('rule-party-greater').checked = Boolean(r.PartyMemberHealthGreaterThanBool ?? r.partyMemberHealthGreaterThanBool);
      $('rule-party-hp').value = r.PartyMemberHealthUseValue ?? r.partyMemberHealthUseValue ?? 0;
      $('rule-party-percent').checked = Boolean(r.PartyMemberHealthIsPercentage ?? r.partyMemberHealthIsPercentage);
      $('rule-multi').checked = Boolean(r.MultiAuraBool ?? r.multiAuraBool);
      $('rule-multi-op').value = r.MultiAuraOperatorIndex ?? r.multiAuraOperatorIndex ?? 0;
      const multi = r.multiAuraChecks ?? r.MultiAuraChecks ?? [];
      $('rule-multi-lines').value = multi
        .map((x) => `${x.AuraName ?? x.auraName}|${x.StackCount ?? x.stackCount}|${x.IsGreater ?? x.isGreater}|${x.AuraTargetIndex ?? x.auraTargetIndex}`)
        .join('\n');
      setStatus('rule-status', result.display ?? text, true);
      return result;
    }

    async function editCurrent() {
      if (selectedCurrent < 0 || selectedCurrent >= currentTokens.length) return;
      try {
        editingCurrent = selectedCurrent;
        await parseIntoRuleEditor(currentTokens[selectedCurrent]);
        $('skills-rule-details').open = true;
        renderCurrentSkills();
      } catch (error) {
        setStatus('rule-status', String(error?.message ?? error));
      }
    }

    function multiAuraInput() {
      return val('rule-multi-lines').split(/\r?\n/)
        .map((line) => line.trim())
        .filter(Boolean)
        .map((line) => {
          const [auraName, stack = '0', greater = 'true', target = '0'] = line.split('|');
          return {
            auraName: auraName.trim(),
            stackCount: Number(stack) || 0,
            isGreater: String(greater).trim().toLowerCase() !== 'false',
            auraTargetIndex: Number(target) || 0
          };
        });
    }

    async function buildRule(skillOverride = null, replaceEditing = false) {
      const result = await cmd('skills.build', [
        skillOverride ?? Number(val('rule-skill')),
        checked('rule-use'),
        Number(val('rule-wait')),
        checked('rule-hp-greater'),
        Number(val('rule-hp')),
        checked('rule-hp-percent'),
        checked('rule-mp-greater'),
        Number(val('rule-mp')),
        checked('rule-mp-percent'),
        checked('rule-aura-greater'),
        Number(val('rule-aura-value')),
        Number(val('rule-aura-target')),
        val('rule-aura'),
        checked('rule-skip'),
        checked('rule-party-greater'),
        Number(val('rule-party-hp')),
        checked('rule-party-percent'),
        checked('rule-multi'),
        Number(val('rule-multi-op')),
        multiAuraInput()
      ]);

      if (replaceEditing && editingCurrent >= 0 && editingCurrent < currentTokens.length) {
        currentTokens[editingCurrent] = result.converted;
        currentDisplays[editingCurrent] = result.display ?? result.converted;
        selectedCurrent = editingCurrent;
        editingCurrent = -1;
      } else {
        currentTokens.push(result.converted);
        currentDisplays.push(result.display ?? result.converted);
        selectedCurrent = currentTokens.length - 1;
      }
      renderCurrentSkills();
      setStatus('rule-status', result.display ?? result.converted, true);
      return result;
    }

    qsa('[data-add-skill]').forEach((button) => button.addEventListener('click', () => void buildRule(Number(button.dataset.addSkill), false).catch((error) => setStatus('rule-status', String(error?.message ?? error)))));
    $('rule-build')?.addEventListener('click', () => void buildRule(null, editingCurrent >= 0).catch((error) => setStatus('rule-status', String(error?.message ?? error))));
    $('rule-parse')?.addEventListener('click', () => void editCurrent());
    $('skills-edit-current')?.addEventListener('click', () => void editCurrent());
    $('skills-remove-current')?.addEventListener('click', removeCurrent);
    $('skills-clear-current')?.addEventListener('click', () => {
      currentTokens = [];
      currentDisplays = [];
      selectedCurrent = -1;
      editingCurrent = -1;
      renderCurrentSkills();
    });
    $('skills-select-up')?.addEventListener('click', () => selectDelta(-1));
    $('skills-select-down')?.addEventListener('click', () => selectDelta(1));
    $('skills-move-up')?.addEventListener('click', () => moveDelta(-1));
    $('skills-move-down')?.addEventListener('click', () => moveDelta(1));

    $('skills-current-list')?.addEventListener('keydown', (event) => {
      if (event.key === 'ArrowUp') {
        event.preventDefault();
        event.ctrlKey ? moveDelta(-1) : selectDelta(-1);
      } else if (event.key === 'ArrowDown') {
        event.preventDefault();
        event.ctrlKey ? moveDelta(1) : selectDelta(1);
      } else if (event.key === 'Delete') {
        event.preventDefault();
        if (event.altKey) {
          currentTokens = [];
          currentDisplays = [];
          selectedCurrent = -1;
          editingCurrent = -1;
          renderCurrentSkills();
        } else {
          removeCurrent();
        }
      } else if (event.key === 'Enter') {
        event.preventDefault();
        void editCurrent();
      }
    });

    $('skills-save')?.addEventListener('click', async () => {
      try {
        syncDsl();
        data = await cmd('skills.save', [
          val('skills-class'),
          val('skills-dsl'),
          Number(val('skills-timeout')),
          val('skills-class-mode'),
          currentUseMode(),
          checked('skills-reset-target')
        ], 30000);
        state.selectedSkill = null;
        renderSaved();
        toast('Advanced Skill set saved.');
      } catch (error) {
        setStatus('skills-status', String(error?.message ?? error));
      }
    });

    $('skills-remove')?.addEventListener('click', async () => {
      const className = val('skills-class');
      const mode = val('skills-class-mode');
      if (!className) return;
      try {
        data = await cmd('skills.remove', [className, mode], 30000);
        state.selectedSkill = null;
        currentTokens = [];
        currentDisplays = [];
        selectedCurrent = -1;
        renderCurrentSkills();
        renderSaved();
      } catch (error) {
        setStatus('skills-status', String(error?.message ?? error));
      }
    });

    $('skills-reset')?.addEventListener('click', () => void cmd('skills.reset', [], 30000).then(refresh).catch((e) => setStatus('skills-status', String(e?.message ?? e))));
    $('skills-sync')?.addEventListener('click', () => void cmd('skills.sync', [], 60000).then(refresh).catch((e) => setStatus('skills-status', String(e?.message ?? e))));
  }

  function bindPacketSpammer() {
    let data=null, selected=-1;
    function render(){
      $('spammer-client').checked=Boolean(data?.sendToClient);$('spammer-delay').value=data?.spamDelay??1000;$('spammer-toggle').textContent=data?.running?'Stop':'Start';
      $('spammer-list').textContent='';(data?.packets??[]).forEach((packet,index)=>{const row=document.createElement('div');row.className='parity-list-row';if(index===selected||index===data.selectedIndex)row.classList.add('selected');row.innerHTML=`<span>${index+1}</span><span class="parity-mono">${esc(packet)}</span><span>${index===data.selectedIndex?'▶':''}</span>`;row.addEventListener('click',()=>{selected=index;$('spammer-packet').value=packet;render();});$('spammer-list').appendChild(row);});setStatus('spammer-status',data?.running?'Spamming packets.':'Stopped.',true);
    }
    async function refresh(){if(!backend())return setStatus('spammer-status','Backend offline.');try{data=await cmd('packets.spammer.status');render();}catch(error){setStatus('spammer-status',String(error?.message??error));}}
    refreshers['window-packet-spammer']=refresh;
    async function configure(){try{data=await cmd('packets.spammer.configure',[checked('spammer-client'),Number(val('spammer-delay'))]);render();}catch(error){setStatus('spammer-status',String(error?.message??error));}}
    $('spammer-client')?.addEventListener('change',()=>void configure());$('spammer-delay')?.addEventListener('change',()=>void configure());
    $('spammer-add')?.addEventListener('click',()=>void cmd('packets.spammer.add',[val('spammer-packet')]).then(d=>{data=d;render();}).catch(e=>setStatus('spammer-status',String(e?.message??e))));
    $('spammer-send')?.addEventListener('click',()=>void cmd('packets.spammer.send',[val('spammer-packet'),checked('spammer-client')]).then(()=>setStatus('spammer-status','Packet sent.',true)).catch(e=>setStatus('spammer-status',String(e?.message??e))));
    $('spammer-remove')?.addEventListener('click',()=>{if(selected<0)return;void cmd('packets.spammer.remove',[selected]).then(d=>{data=d;selected=-1;render();}).catch(e=>setStatus('spammer-status',String(e?.message??e)));});
    $('spammer-clear')?.addEventListener('click',()=>void cmd('packets.spammer.clear').then(d=>{data=d;selected=-1;render();}).catch(e=>setStatus('spammer-status',String(e?.message??e))));
    $('spammer-toggle')?.addEventListener('click',()=>void cmd(data?.running?'packets.spammer.stop':'packets.spammer.start',[],30000).then(d=>{data=d;render();}).catch(e=>setStatus('spammer-status',String(e?.message??e))));
    $('spammer-save')?.addEventListener('click',async()=>{try{await shell('dialog.save-text',['Save Packet List','packets.txt',(data?.packets??[]).join('\n'),'txt']);toast('Packet list saved.');}catch(error){setStatus('spammer-status',String(error?.message??error));}});
    $('spammer-load')?.addEventListener('click',async()=>{try{const pick=await shell('dialog.open-file',['Load Packet List','.', ['txt'],false]);if(!pick?.path)return;const read=await shell('shell.read-text',[pick.path]);for(const line of String(read.content??'').split(/\r?\n/).map(x=>x.trim()).filter(Boolean)) data=await cmd('packets.spammer.add',[line]);render();toast('Packet list loaded.');}catch(error){setStatus('spammer-status',String(error?.message??error));}});
  }

  function bindPacketLogger() {
    let data=null;
    function render(){
      $('packet-logger-enabled').checked=Boolean(data?.enabled);$('packet-logger-filters').innerHTML=(data?.filters??[]).map(f=>`<label><input type="checkbox" data-packet-filter="${esc(f.name)}" ${f.isChecked?'checked':''}> ${esc(f.name)}</label>`).join('');qsa('[data-packet-filter]').forEach(box=>box.addEventListener('change',()=>void cmd('packets.logger.filters',[box.dataset.packetFilter,box.checked]).then(d=>{data=d;render();}).catch(e=>setStatus('packet-logger-status',String(e?.message??e)))));
      const query=val('packet-logger-search').trim().toLowerCase();$('packet-logger-log').textContent=(data?.logs??[]).filter(line=>!query||String(line).toLowerCase().includes(query)).join('\n');setStatus('packet-logger-status',`${data?.logs?.length??0} packet(s).`,true);
    }
    async function refresh(){if(!backend())return;try{data=await cmd('packets.logger.status');render();}catch(error){setStatus('packet-logger-status',String(error?.message??error));}}
    refreshers['window-packet-logger']=refresh;
    $('packet-logger-enabled')?.addEventListener('change',()=>void cmd('packets.logger.enable',[checked('packet-logger-enabled')]).then(d=>{data=d;render();}).catch(e=>setStatus('packet-logger-status',String(e?.message??e))));$('packet-logger-search')?.addEventListener('input',render);
    $('packet-logger-clear')?.addEventListener('click',()=>void cmd('packets.logger.clear').then(d=>{data=d;render();}).catch(e=>setStatus('packet-logger-status',String(e?.message??e))));
    $('packet-logger-save')?.addEventListener('click',()=>void shell('dialog.save-text',['Save Packet Log','packet-log.txt',(data?.logs??[]).join('\n'),'txt']).catch(e=>setStatus('packet-logger-status',String(e?.message??e))));
    clearInterval(state.packetLoggerTimer);state.packetLoggerTimer=setInterval(()=>{if($('window-packet-logger')?.classList.contains('open'))void refresh();},1000);
  }

  function bindPacketInterceptor() {
    let data = null;
    let serverData = null;

    function appendPackets(items) {
      const host = $('interceptor-log');
      for (const item of items ?? []) {
        const line = document.createElement('div');
        line.textContent = `#${item.id} ${item.outbound === true ? '→' : item.outbound === false ? '←' : '×'} ${item.packet}`;
        host.appendChild(line);
      }
      while (host.children.length > 5000) host.firstChild.remove();
      if (items?.length) host.scrollTop = host.scrollHeight;
    }

    function renderServers() {
      const select = $('interceptor-server');
      if (!select || !serverData) return;
      const previous = select.value || serverData.selectedServer || '';
      select.innerHTML = '<option value="">Select Server</option>' +
        (serverData.servers ?? []).map((server) => {
          const label = `${server.name} - ${server.playerCount ?? 0}${server.maxPlayers ? `/${server.maxPlayers}` : ''}${server.upgrade ? ' · Member' : ''}`;
          return `<option value="${esc(server.name)}" ${server.name === previous ? 'selected' : ''} ${server.online === false ? 'disabled' : ''}>${esc(label)}</option>`;
        }).join('');
    }

    function renderControls() {
      if (!data) return;
      $('interceptor-enabled').checked = Boolean(data.enabled);
      $('interceptor-logging').checked = Boolean(data.isLogging);
      $('interceptor-filters').innerHTML = (data.filters ?? [])
        .map((filter) => `<label><input type="checkbox" data-interceptor-filter="${esc(filter.name)}" ${filter.isChecked ? 'checked' : ''}> ${esc(filter.name)}</label>`)
        .join('');

      qsa('[data-interceptor-filter]').forEach((box) => box.addEventListener('change', () => void shell('interceptor.filter', [box.dataset.interceptorFilter, box.checked]).then((result) => {
        data = { ...data, ...result };
        renderControls();
      }).catch((error) => setStatus('interceptor-status', String(error?.message ?? error)))));

      const selected = val('interceptor-server');
      const serverLabel = selected ? ` · target ${selected}` : '';
      const transport = data.host ? ` · transport ${data.host}:${data.port}` : '';
      setStatus(
        'interceptor-status',
        `${data.running ? 'Connected' : data.enabled ? 'Waiting for transport' : 'Idle'}${serverLabel}${transport}`,
        Boolean(data.running)
      );
    }

    async function refreshServers() {
      if (!backend()) return;
      try {
        serverData = await cmd('packets.interceptor.servers', [], 15000);
        renderServers();
      } catch (error) {
        setStatus('interceptor-status', `Server list error: ${error?.message ?? error}`);
      }
    }

    async function refresh(reset = false) {
      if (reset) {
        state.interceptorCursor = 0;
        $('interceptor-log').textContent = '';
      }
      try {
        data = await shell('interceptor.status', [state.interceptorCursor, val('interceptor-search')], 5000);
        appendPackets(data.packets);
        state.interceptorCursor = Math.max(state.interceptorCursor, Number(data.lastId ?? 0));
        renderControls();
      } catch (error) {
        setStatus('interceptor-status', String(error?.message ?? error));
      }
    }

    refreshers['window-packet-interceptor'] = async () => {
      await Promise.all([refresh(true), refreshServers()]);
    };

    $('interceptor-enabled')?.addEventListener('change', async () => {
      const enabled = checked('interceptor-enabled');
      try {
        data = { ...data, ...(await shell('interceptor.enable', [enabled])) };
        renderControls();

        if (enabled) {
          const serverName = val('interceptor-server');
          if (!serverName) {
            data = { ...data, ...(await shell('interceptor.enable', [false])) };
            renderControls();
            throw new Error('Select a server before connecting the interceptor.');
          }
          if (!backend()) {
            data = { ...data, ...(await shell('interceptor.enable', [false])) };
            renderControls();
            throw new Error('Backend offline.');
          }

          setStatus('interceptor-status', `Relogging into ${serverName} through the Linux inline interceptor...`);
          const result = await cmd('packets.interceptor.relogin', [serverName], 60000);
          if (!result?.connected) {
            data = { ...data, ...(await shell('interceptor.enable', [false])) };
            renderControls();
            throw new Error(`Could not connect to ${serverName}.`);
          }
          serverData = { ...(serverData ?? {}), selectedServer: serverName };
          renderServers();
          await refresh(false);
        }
      } catch (error) {
        setStatus('interceptor-status', String(error?.message ?? error));
      }
    });

    $('interceptor-logging')?.addEventListener('change', () => void shell('interceptor.logging', [checked('interceptor-logging')]).then((result) => {
      data = { ...data, ...result };
      renderControls();
    }).catch((error) => setStatus('interceptor-status', String(error?.message ?? error))));

    $('interceptor-clear-filters')?.addEventListener('click', () => void shell('interceptor.filter', ['__clear__', false]).then((result) => {
      data = { ...data, ...result };
      renderControls();
    }).catch((error) => setStatus('interceptor-status', String(error?.message ?? error))));

    $('interceptor-clear')?.addEventListener('click', () => void shell('interceptor.clear').then((result) => {
      state.interceptorCursor = Number(result.lastId ?? 0);
      $('interceptor-log').textContent = '';
    }).catch((error) => setStatus('interceptor-status', String(error?.message ?? error))));

    $('interceptor-search')?.addEventListener('change', () => void refresh(true));
    $('interceptor-server')?.addEventListener('change', renderControls);

    clearInterval(state.interceptorTimer);
    state.interceptorTimer = setInterval(() => {
      if ($('window-packet-interceptor')?.classList.contains('open')) void refresh(false);
    }, 500);
  }

  function bindLogs() {
    let type='Debug';
    async function refresh(){if(!backend())return;try{const data=await cmd('logs.status',[type]);$('global-log').textContent=(data.logs??[]).join('\n');setStatus('logs-status',`${type} · ${data.logs?.length??0} entries`,true);}catch(error){setStatus('logs-status',String(error?.message??error));}}
    refreshers['window-logs']=refresh;
    qsa('[data-log-type]').forEach(tab=>tab.addEventListener('click',()=>{qsa('[data-log-type]').forEach(x=>x.classList.toggle('active',x===tab));type=tab.dataset.logType;void refresh();}));
    $('logs-refresh')?.addEventListener('click',()=>void refresh());
    // Original initializeActions owns copy/save buttons. Replace clear behavior with Core clear as well.
    $('global-log-clear')?.addEventListener('click',()=>void cmd('logs.clear',[type]).then(refresh).catch(e=>setStatus('logs-status',String(e?.message??e))));
    clearInterval(state.logsTimer);state.logsTimer=setInterval(()=>{if($('window-logs')?.classList.contains('open'))void refresh();},1000);
  }

  function bindPlugins() {
    let data=null;
    function render(){const query=val('plugins-search').trim().toLowerCase();const list=(data?.plugins??[]).filter(p=>!query||`${p.name} ${p.author} ${p.description}`.toLowerCase().includes(query));$('plugins-list').textContent='';for(const plugin of list){const row=document.createElement('div');row.className='parity-list-row';row.innerHTML=`<span>●</span><span><strong>${esc(plugin.name)}</strong> <span class="parity-pill">${esc(plugin.author??'')}</span><br><span class="parity-option-desc">${esc(plugin.description??'')}</span></span><span class="parity-actions"><button class="skua-button secondary" data-plugin-options="${esc(plugin.name)}" ${plugin.hasOptions?'':'disabled'}>Options</button><button class="skua-button secondary" data-plugin-unload="${esc(plugin.name)}">Unload</button></span>`;$('plugins-list').appendChild(row);}qsa('[data-plugin-unload]').forEach(btn=>btn.addEventListener('click',()=>void cmd('plugins.unload',[btn.dataset.pluginUnload],30000).then(d=>{data=d;render();}).catch(e=>setStatus('plugins-status',String(e?.message??e)))));qsa('[data-plugin-options]').forEach(btn=>btn.addEventListener('click',()=>void openPluginOptions(btn.dataset.pluginOptions)));setStatus('plugins-status',`${data?.plugins?.length??0} plugin(s) · ${data?.pluginsDirectory??''}`,true);}
    async function refresh(){if(!backend())return setStatus('plugins-status','Backend offline.');try{data=await cmd('plugins.status');render();}catch(error){setStatus('plugins-status',String(error?.message??error));}}
    refreshers['window-plugins']=refresh;$('plugins-search')?.addEventListener('input',render);
    $('plugins-load')?.addEventListener('click',async()=>{try{if(!data)await refresh();const pick=await shell('dialog.open-file',['Load Plugin',data?.pluginsDirectory??'.',['dll'],false]);if(!pick?.path)return;data=await cmd('plugins.load',[pick.path],30000);render();toast('Plugin loaded.');}catch(error){setStatus('plugins-status',String(error?.message??error));}});
    $('plugins-unload-all')?.addEventListener('click',()=>void cmd('plugins.unloadAll',[],30000).then(d=>{data=d;render();}).catch(e=>setStatus('plugins-status',String(e?.message??e))));
    async function openPluginOptions(name){state.selectedPlugin=name;try{const options=await cmd('plugins.options.status',[name]);renderOptionGroups($('plugin-options-body'),options.options,async(option,value,control)=>{control.disabled=true;try{await cmd('plugins.options.set',[name,option.category,option.name,String(value)]);setStatus('plugin-options-status',`${option.displayName??option.name} saved.`,true);}catch(error){setStatus('plugin-options-status',String(error?.message??error));}finally{control.disabled=false;}});setStatus('plugin-options-status',name,true);state.api.openWindow('window-plugin-options');}catch(error){setStatus('plugins-status',String(error?.message??error));}}
  }

  function gestureForKeyboardEvent(event){const parts=[];if(event.ctrlKey)parts.push('Ctrl');if(event.altKey)parts.push('Alt');if(event.shiftKey)parts.push('Shift');if(event.metaKey)parts.push('Meta');let key=event.key.length===1?event.key.toUpperCase():event.key.replace(/^Arrow/,'');if(['Control','Shift','Alt','Meta'].includes(key))return '';parts.push(key);return parts.join('+');}
  function bindJumpParity(){
    const cell=$('jump-cell');
    const pad=$('jump-pad');
    if(!cell||!pad)return;

    cell.addEventListener('change',async()=>{
      const selectedCell=cell.value;
      if(!selectedCell)return;

      if(selectedCell==='Enter')pad.value='Spawn';
      else if(!pad.value)pad.value='Left';

      try{
        const result=await cmd('jump.execute',[selectedCell,pad.value||'Left'],15000);
        const status=$('jump-status');
        if(status)status.textContent=`${result?.map??''} · ${selectedCell} · ${pad.value||'Left'}`;
      }catch(error){
        log(`Jump failed: ${error?.message??error}`,'error');
      }
    });
  }

  function bindHotKeyExecution(){
    window.addEventListener('keydown',async(event)=>{
      if(event.repeat||event.target?.dataset?.hotkey)return;const gesture=gestureForKeyboardEvent(event);if(!gesture)return;const entry=Object.entries(state.hotKeys).find(([,g])=>String(g).toLowerCase()===gesture.toLowerCase());if(!entry)return;event.preventDefault();const action=entry[0];
      try{
        if(action==='LoadScript')$('script-load-button')?.click();
        else if(action==='OpenBank')$('bank-open-button')?.click();
        else if(action==='OpenConsole')state.api.openWindow('window-console');
        else if(action==='ToggleAutoAttack')$('auto-attack-switch')?.click();
        else if(action==='ToggleAutoHunt')$('auto-hunt-switch')?.click();
        else if(action==='ToggleScript'){const script=state.api.getScriptState();await cmd(script.running?'script.stop':'script.start',[],60000);await state.api.refreshScriptStatus(true);}
        else if(action==='ToggleLagKiller'){const data=await cmd('options.game.status');const option=(data.options??[]).find(x=>String(x.name).toLowerCase()==='lagkiller');if(option)await cmd('options.game.set',[option.name,!Boolean(option.value)]);}
      }catch(error){log(`HotKey ${action} failed: ${error?.message??error}`,'error');}
    });
  }
})();
