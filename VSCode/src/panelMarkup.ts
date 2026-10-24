//The body of the extension page
//Every fixed string names a translation key and the gui layer writes it, so this file holds structure only
export const panelMarkup = `
    <div class="shell" id="shell">
        <header class="region">
            <span class="brand" data-text="page.title"></span>
            <span class="hint" data-text="page.subtitle"></span>
        </header>
        <nav class="region">
            <div class="nav-item active" data-page="home" data-text="nav.home"></div>
            <div class="nav-item" data-page="rules" data-text="nav.rules"></div>
            <div class="nav-item" data-page="nuget" data-text="nav.nuget"></div>
            <div class="nav-item" data-page="projectRef" data-text="nav.projectRef"></div>
            <div class="nav-item" data-page="assemblyRef" data-text="nav.assemblyRef"></div>
            <div class="nav-item" data-page="modRef" data-text="nav.modRef"></div>
            <div class="nav-item" data-page="diagnostics" data-text="nav.diagnostics"></div>
            <div class="nav-item" data-page="asm" data-text="nav.asm"></div>
            <div class="nav-item" data-page="config" data-text="nav.config"></div>
            <div class="nav-item" data-page="tasks" data-text="nav.tasks"></div>
            <div class="nav-item" data-page="settings" data-text="nav.settings"></div>
        </nav>
        <main class="region">
            <section class="page" id="page-home">
                <div id="home-project" class="home hidden">
                    <div class="home-head">
                        <img id="home-icon" class="home-icon hidden" alt="">
                        <div class="home-title">
                            <span id="home-name" class="home-name"></span>
                            <span id="home-meta" class="home-meta"></span>
                        </div>
                    </div>
                    <div id="home-card" class="card"></div>
                </div>
                <div id="home-empty" class="empty hidden">
                    <span data-text="home.noProject"></span>
                    <button class="create" id="create">+ <span data-text="home.create"></span></button>
                </div>
            </section>
            <section class="page hidden" id="page-rules">
                <div id="rule-list" class="rules"></div>
                <div id="rule-empty" class="placeholder hidden" data-text="rules.empty"></div>
            </section>
            <section class="page hidden" id="page-nuget">
                <div class="nuget">
                    <div class="nuget-tabs">
                        <span class="nuget-tab active" data-ntab="installed" data-text="nuget.installed"></span>
                        <span class="nuget-tab" data-ntab="browse" data-text="nuget.browse"></span>
                    </div>
                    <div class="nuget-body">
                        <div class="nuget-main">
                            <div class="nuget-bar hidden" id="nuget-bar">
                                <input id="nuget-query" spellcheck="false" autocomplete="off">
                                <select id="nuget-source" class="hidden"></select>
                                <button id="nuget-refresh" class="nuget-refresh" title=""></button>
                                <button id="nuget-search" data-text="nuget.search"></button>
                            </div>
                            <div class="nuget-status" id="nuget-status"></div>
                            <div class="nuget-box" id="nuget-box"></div>
                        </div>
                        <div class="nuget-split collapsed" id="nuget-split"></div>
                        <aside class="nuget-detail collapsed" id="nuget-detail"></aside>
                    </div>
                </div>
            </section>
            <section class="page hidden" id="page-projectRef">
                <div class="refs">
                    <p class="refs-hint" data-text="refs.project.hint"></p>
                    <button id="add-project" data-text="refs.project.add"></button>
                </div>
            </section>
            <section class="page hidden" id="page-assemblyRef">
                <div class="refs">
                    <p class="refs-hint" data-text="refs.assembly.hint"></p>
                    <button id="add-assembly" data-text="refs.assembly.add"></button>
                </div>
            </section>
            <section class="page hidden" id="page-modRef">
                <div class="placeholder" data-text="planned.mod"></div>
            </section>
            <section class="page hidden" id="page-diagnostics">
                <div id="diagnostic-list" class="diagnostics"></div>
                <div id="diagnostic-empty" class="placeholder hidden" data-text="diagnostics.empty"></div>
            </section>
            <section class="page hidden" id="page-asm">
                <div class="asm">
                    <div class="asm-bar">
                        <input id="asm-file" spellcheck="false" autocomplete="off">
                        <button id="asm-browse" data-text="asm.browse"></button>
                        <button id="asm-load" data-text="asm.load"></button>
                    </div>
                    <div class="asm-body">
                        <div class="asm-side">
                            <input id="asm-filter" class="asm-filter" spellcheck="false" autocomplete="off">
                            <div class="asm-info" id="asm-info"></div>
                            <div class="asm-types" id="asm-types"></div>
                        </div>
                        <div class="asm-main">
                            <div class="asm-tabs">
                                <span class="asm-tab active" data-tab="code" data-text="asm.code"></span>
                                <span class="asm-tab" data-tab="il" data-text="asm.il"></span>
                                <span class="asm-tab" data-tab="detail" data-text="asm.detail"></span>
                                <span class="asm-tab" data-tab="res" data-text="asm.resources"></span>
                            </div>
                            <div class="asm-pane">
                                <pre id="asm-text" class="asm-text"></pre>
                                <div id="asm-res" class="asm-res hidden">
                                    <div class="asm-res-bar">
                                        <button id="asm-add" data-text="asm.addRes"></button>
                                    </div>
                                    <div id="asm-res-list" class="asm-res-list"></div>
                                </div>
                            </div>
                            <div class="asm-status" id="asm-status"></div>
                        </div>
                    </div>
                </div>
            </section>
            <section class="page hidden" id="page-config">
                <div class="config">
                    <p class="config-hint" data-text="config.hint"></p>
                    <div id="config-list" class="config-list"></div>
                    <div id="config-empty" class="placeholder hidden" data-text="config.empty"></div>
                </div>
            </section>
            <section class="page hidden" id="page-tasks">
                <div class="tasks" id="tasks-main">
                    <div class="tasks-top">
                        <label class="tasks-switch">
                            <input type="checkbox" id="tasks-override">
                            <span class="tasks-track"><span class="tasks-knob"></span></span>
                            <span class="tasks-switch-label" data-text="tasks.override"></span>
                            <span class="tasks-switch-state" id="tasks-override-state"></span>
                        </label>
                        <div class="tasks-actions">
                            <button id="tasks-add" data-text="tasks.add"></button>
                            <button id="tasks-add-script" data-text="tasks.addScript"></button>
                            <button id="tasks-delete" class="tasks-delete hidden" data-text="tasks.remove"></button>
                        </div>
                    </div>
                    <div class="tasks-status" id="tasks-status"></div>
                    <div class="tasks-body">
                        <div class="tasks-list-col">
                            <div class="task-list" id="task-list"></div>
                            <div class="placeholder hidden" id="tasks-empty" data-text="tasks.empty"></div>
                        </div>
                        <div class="tasks-split collapsed" id="tasks-split"></div>
                        <aside class="task-detail collapsed" id="task-detail">
                            <div class="task-detail-bar">
                                <span class="task-detail-title" id="task-detail-title"></span>
                                <span class="task-editor-status" id="task-editor-status"></span>
                                <button id="tasks-save" data-text="tasks.save"></button>
                                <button id="tasks-cancel" data-text="tasks.cancel"></button>
                            </div>
                            <div class="task-detail-tabs">
                                <span class="task-detail-tab active" data-ttab="visual" data-text="tasks.visual"></span>
                                <span class="task-detail-tab" data-ttab="xml" data-text="tasks.xml"></span>
                            </div>
                            <div class="task-visual" id="task-visual">
                                <div class="task-fields" id="task-fields"></div>
                                <div class="task-steps" id="task-steps"></div>
                                <div class="task-blocks">
                                    <span class="task-blocks-label" data-text="tasks.blocks"></span>
                                    <button data-block="exec" data-text="tasks.block.exec"></button>
                                    <button data-block="copy" data-text="tasks.block.copy"></button>
                                    <button data-block="zip" data-text="tasks.block.zip"></button>
                                    <button data-block="tool" data-text="tasks.block.tool"></button>
                                </div>
                            </div>
                            <div class="task-editor-body hidden" id="task-xml-pane">
                                <textarea id="tasks-text" spellcheck="false"></textarea>
                            </div>
                            <div class="task-editor-body hidden" id="task-script-pane">
                                <div class="task-script-hint">
                                    <span data-text="tasks.scriptHint"></span>
                                    <span class="task-script-error" id="task-script-status"></span>
                                </div>
                                <textarea id="tasks-script" spellcheck="false"></textarea>
                            </div>
                        </aside>
                    </div>
                </div>
            </section>
            <section class="page hidden" id="page-settings">
                <div id="setting-list"></div>
                <div class="actions">
                    <button id="open-json" data-text="settings.openJson"></button>
                </div>
            </section>
        </main>
        <div class="log" id="log">
            <div class="log-resize" id="log-resize"></div>
            <div class="log-list" id="log-list"></div>
        </div>
        <footer class="region" id="status">
            <span id="status-line"></span>
        </footer>
    </div>
    <div class="dialog hidden" id="dialog">
        <div class="dialog-body">
            <h2 data-text="create.title"></h2>
            <div class="field"><span data-text="create.name"></span><input id="f-name"></div>
            <div class="field"><span data-text="create.id"></span><input id="f-id"></div>
            <div class="field"><span data-text="create.description"></span><input id="f-description"></div>
            <div class="field"><span data-text="create.authors"></span><input id="f-authors"></div>
            <div class="field"><span data-text="create.homepage"></span><input id="f-homepage"></div>
            <div class="field"><span data-text="create.sources"></span><input id="f-sources"></div>
            <div class="field"><span data-text="create.license"></span><input id="f-license"></div>
            <label class="check">
                <input type="checkbox" id="f-backup">
                <span data-text="create.backup"></span>
            </label>
            <div class="dialog-status" id="dialog-status"></div>
            <div class="dialog-actions">
                <button id="cancel" data-text="create.cancel"></button>
                <button id="confirm" data-text="create.confirm"></button>
            </div>
        </div>
    </div>
`;
