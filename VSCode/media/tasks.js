"use strict";

//The task page lists what the project config declares
//A row selects on the first click and runs on the second; the pencil opens that task in the side pane

let taskEntries = [];
let taskOverrides = false;
let taskSelected = "";
let taskRunning = "";
let taskError = "";
let taskNotice = "";
let taskSaving = false;
let taskDeleting = "";
let taskEditor = null;
let taskScriptEditor = null;
let taskDetailMode = "xml";
let taskScriptSource = "";
let taskEditingName = "";
let taskEditingConfig = "";
let taskPendingOpen = "";
let taskResizing = false;
let taskTab = "visual";
let taskModel = null;
let tmGrammar = null;
let tmLines = [];
let tmTimer = 0;

//loadTasks asks the server what the project declares
function loadTasks() {
    vscode.postMessage({ type: "tasks" });
}

//applyTasks fills the page from one answer of the server
function applyTasks(message) {
    if (!message.ok) {
        taskError = message.error || gui.texts["tasks.failed"];
        if (taskDeleting.length > 0) {
            taskDeleting = "";
            taskError = message.error || gui.texts["tasks.removeFailed"];
        }
        if (taskSaving) {
            taskSaving = false;
            setTaskEditorStatus(message.error || gui.texts["tasks.saveFailed"]);
        }
        drawTasks();
        return;
    }

    taskEntries = message.tasks || [];
    taskOverrides = message.overrides === true;
    taskError = "";

    if (taskDeleting.length > 0) {
        const name = taskDeleting;
        taskDeleting = "";

        if (message.removed === true) {
            if (taskSelected === name) {
                taskSelected = "";
            }
            if (taskEditingName === name) {
                taskEditingName = "";
                closeTaskDetail();
            }
            taskNotice = gui.texts["tasks.removed"];
        } else {
            taskError = message.error || gui.texts["tasks.removeFailed"];
        }
    }

    if (taskSaving) {
        taskSaving = false;
        const pending = taskPendingOpen;
        taskPendingOpen = "";
        closeTaskDetail();

        if (pending.length > 0) {
            const created = taskEntries.find((task) => task.name === pending);
            if (created) {
                openTaskDetail(created);
            }
        } else {
            taskNotice = gui.texts["tasks.saved"];
        }
    }

    drawTasks();
}

//applyTaskStarted clears the running row when the server refused to start the task
function applyTaskStarted(message) {
    if (message.ok) {
        return;
    }

    taskRunning = "";
    if (message.error) {
        taskError = message.error;
    }
    drawTasks();
}

//applyTaskFinished clears the running row and reports how the task ended
function applyTaskFinished(message) {
    if (message.name !== taskRunning) {
        return;
    }

    taskRunning = "";
    if (message.ok) {
        taskNotice = gui.texts["tasks.done"];
    } else {
        taskError = lastLine(message.output) || gui.texts["tasks.failed"];
    }
    drawTasks();
}

//lastLine keeps the end of the tool output, which names the failure
function lastLine(output) {
    const lines = String(output || "")
        .split(/\r?\n/)
        .map((line) => line.trim())
        .filter((line) => line.length > 0);
    return lines.length > 0 ? lines[lines.length - 1] : "";
}

//drawTasks writes the switch, the status line and every task row
function drawTasks() {
    const state = document.getElementById("tasks-override-state");
    state.textContent = taskOverrides
        ? gui.texts["tasks.on"]
        : gui.texts["tasks.off"];
    state.classList.toggle("on", taskOverrides);
    document.getElementById("tasks-override").checked = taskOverrides;

    const status = document.getElementById("tasks-status");
    const text = taskError
        ? taskError
        : taskNotice
          ? taskNotice
          : taskRunning
            ? gui.texts["tasks.running"] + " " + taskRunning
            : "";
    status.textContent = text;
    status.className =
        "tasks-status" +
        (taskError ? " error" : taskNotice ? " ok" : "");

    //the delete entry only exists while a declared task is selected
    const remove = document.getElementById("tasks-delete");
    const deletable =
        taskSelected.length > 0 &&
        taskEntries.some((task) => task.name === taskSelected);
    remove.classList.toggle("hidden", !deletable);
    remove.disabled = taskDeleting.length > 0;

    const list = document.getElementById("task-list");
    list.textContent = "";
    list.classList.toggle("hidden", taskEntries.length === 0);
    document.getElementById("tasks-empty").classList.toggle(
        "hidden",
        taskEntries.length > 0
    );

    for (const task of taskEntries) {
        list.append(taskRow(task));
    }
}

//taskRow builds one task: the row selects and runs, the pencil edits
function taskRow(task) {
    const row = document.createElement("div");
    row.className = "task-row";
    row.classList.toggle("shadowed", task.shadowed);
    row.classList.toggle("running", task.name === taskRunning);
    row.classList.toggle("selected", task.name === taskSelected);
    row.classList.toggle("blocked", taskRunning.length > 0);
    row.title =
        task.name === taskSelected
            ? gui.texts["tasks.runHint"]
            : gui.texts["tasks.selectHint"];

    const head = document.createElement("div");
    head.className = "task-head";

    const name = document.createElement("span");
    name.className = "task-name";
    name.textContent = task.name;

    const description = document.createElement("span");
    description.className = "task-desc";
    description.textContent = task.description || gui.texts["tasks.default"];

    const edit = document.createElement("button");
    edit.className = "task-edit";
    edit.textContent = "\u270E";
    edit.title = gui.texts["tasks.edit"];
    edit.addEventListener("click", (event) => {
        event.stopPropagation();
        openTaskDetail(task);
    });

    const run = document.createElement("span");
    run.className = "task-run";
    run.textContent =
        task.name === taskRunning ? gui.texts["tasks.running"] : ">";

    head.append(name, description, edit, run);
    row.append(head);

    const meta = document.createElement("div");
    meta.className = "task-meta";

    if (task.depends.length > 0) {
        const depends = document.createElement("span");
        depends.className = "task-tag";
        depends.textContent =
            gui.texts["tasks.depends"] + " " + task.depends.join(", ");
        meta.append(depends);
    }

    if (task.overrides) {
        const override = document.createElement("span");
        override.className = "task-tag task-tag-override";
        override.textContent = gui.texts["tasks.overrideTag"];
        meta.append(override);
    }

    if (task.shadowed) {
        const shadowed = document.createElement("span");
        shadowed.className = "task-tag task-tag-shadowed";
        shadowed.textContent = gui.texts["tasks.shadowed"];
        meta.append(shadowed);
    }

    if (task.steps.length > 0) {
        const steps = document.createElement("span");
        steps.className = "task-steps";
        steps.textContent = task.steps.join("  |  ");
        steps.title = task.steps.join("\n");
        meta.append(steps);
    }

    row.append(meta);
    row.addEventListener("click", () => {
        if (taskRunning.length > 0) {
            return;
        }

        //the first click only selects, which keeps a stray click from starting a command
        if (task.name === taskSelected) {
            runTask(task);
            return;
        }

        taskSelected = task.name;
        taskNotice = "";
        drawTasks();
    });
    return row;
}

//runTask asks the server to start one task, one at a time
function runTask(task) {
    if (taskRunning.length > 0) {
        return;
    }

    taskRunning = task.name;
    taskError = "";
    taskNotice = "";
    drawTasks();
    vscode.postMessage({ type: "taskRun", name: task.name });
}

//createTask writes a default task straight away, then opens it for editing
function createTask() {
    const name = newTaskName();
    const xml =
        '<Task Name="' +
        name +
        '" Description="">\n' +
        "  <Exec>echo hello</Exec>\n" +
        "</Task>";

    taskPendingOpen = name;
    taskEditingConfig = "";
    taskSaving = true;
    taskError = "";
    taskNotice = "";
    drawTasks();
    vscode.postMessage({ type: "tasksSaveTask", name: "", xml });
}

//deleteTask removes the selected task from the project config
function deleteTask() {
    if (taskSelected.length === 0) {
        return;
    }

    taskDeleting = taskSelected;
    taskError = "";
    taskNotice = "";
    drawTasks();
    const entry = taskEntries.find((task) => task.name === taskDeleting);
    vscode.postMessage({
        type: "tasksRemove",
        name: taskDeleting,
        config: entry ? entry.config : "",
    });
}

//createScriptTask writes a javascript task with a hello line, then opens it
function createScriptTask() {
    const name = newTaskName();
    const source = '// Write the task here.\nlog.info("hello");\n';
    const xml =
        '<Task Name="' +
        name +
        '" Description="">\n' +
        "  <Script><![CDATA[" +
        source +
        "]]></Script>\n" +
        "</Task>";

    taskPendingOpen = name;
    taskEditingConfig = "";
    taskSaving = true;
    taskError = "";
    taskNotice = "";
    drawTasks();
    vscode.postMessage({ type: "tasksSaveTask", name: "", xml });
}

//openTaskDetail shows one task in the side pane
function openTaskDetail(task) {
    taskEditingName = task.name;
    taskEditingConfig = task.config || "";
    taskSelected = task.name;
    document.getElementById("tasks-split").classList.remove("collapsed");
    document.getElementById("task-detail").classList.remove("collapsed");

    const script = taskScriptOf(task.xml);
    taskDetailMode = script === null ? "xml" : "script";

    try {
        taskModel = parseTaskXml(task.xml);
    } catch (error) {
        taskModel = emptyTask(task.name);
        setTaskEditorStatus(String(error));
    }

    document.getElementById("task-detail-title").textContent = task.name;
    document.getElementById("task-script-pane").classList.toggle("hidden", taskDetailMode !== "script");
    document.getElementById("task-detail").classList.toggle("script-mode", taskDetailMode === "script");

    if (taskDetailMode === "script") {
        taskScriptSource = script;
        document.getElementById("task-visual").classList.remove("hidden");
        document.getElementById("task-xml-pane").classList.add("hidden");
        renderTaskVisual();
        ensureTaskScriptEditor();
        taskScriptEditor.setValue(script);
        setTaskEditorStatus("");
        setTaskScriptStatus("");
        scheduleScriptCheck(taskScriptEditor);
        drawTasks();

        //the editor measures itself only after its container is visible
        setTimeout(() => taskScriptEditor.refresh(), 0);
        return;
    }

    ensureTaskEditor();
    taskEditor.setValue(task.xml || buildTaskXml(taskModel));
    setTaskTab("visual", true);
    setTaskEditorStatus("");
    drawTasks();

    setTimeout(() => taskEditor.refresh(), 0);
}

//taskScriptOf returns the script body of a javascript task, null for any other task
function taskScriptOf(xml) {
    if (!xml) {
        return null;
    }

    try {
        const document = new DOMParser().parseFromString(xml, "application/xml");
        if (!document.documentElement || document.documentElement.nodeName === "parsererror") {
            return null;
        }

        for (const child of document.documentElement.children) {
            if (child.localName.toLowerCase() === "script") {
                return child.textContent;
            }
        }
    } catch (error) {
        return null;
    }

    return null;
}

//closeTaskDetail collapses the pane, which animates its width back to nothing
function closeTaskDetail() {
    document.getElementById("task-detail").classList.add("collapsed");
    document.getElementById("tasks-split").classList.add("collapsed");
    document.getElementById("task-detail").classList.remove("script-mode");
    document.getElementById("task-visual").classList.remove("hidden");
    document.getElementById("task-xml-pane").classList.add("hidden");
    document.getElementById("task-script-pane").classList.add("hidden");
    setTaskEditorStatus("");
    drawTasks();
}

//ensureTaskEditor creates the code mirror instance once
function ensureTaskEditor() {
    if (taskEditor) {
        return;
    }

    taskEditor = CodeMirror.fromTextArea(
        document.getElementById("tasks-text"),
        {
            mode: "xml",
            lineNumbers: true,
            matchBrackets: true,
            matchTags: true,
            autoCloseTags: true,
            styleActiveLine: true,
            indentUnit: 2,
            tabSize: 2,
            lineWrapping: false,
        }
    );
    taskEditor.setSize("100%", "100%");
    taskEditor.on("change", () => scheduleTextMate(taskEditor));

    if (tmGrammar) {
        taskEditor.setOption("mode", "ncxml-tm");
        tokenizeTextMate(taskEditor);
    }
}

//ensureTaskScriptEditor creates the javascript editor once
function ensureTaskScriptEditor() {
    if (taskScriptEditor) {
        return;
    }

    taskScriptEditor = CodeMirror.fromTextArea(
        document.getElementById("tasks-script"),
        {
            mode: "ncjs",
            lineNumbers: true,
            matchBrackets: true,
            styleActiveLine: true,
            indentUnit: 2,
            tabSize: 2,
            lineWrapping: false,
            hintOptions: { completeSingle: false },
            extraKeys: {
                "Ctrl-Space": (editor) => requestScriptCompletion(editor),
            },
        }
    );
    taskScriptEditor.setSize("100%", "100%");

    //a dot is the point where the next name is predictable, so the list opens on its own
    taskScriptEditor.on("inputRead", (editor, change) => {
        scheduleScriptCheck(editor);
        if (change.text[0] === ".") {
            requestScriptCompletion(editor);
        }
    });
}

//scriptHintState is the list the language server returned for the current cursor
let scriptHintState = null;
let scriptHintRequest = 0;
let scriptHintPending = null;
let scriptCheckRequest = 0;
let scriptCheckTimer = 0;

//requestScriptCompletion asks the language server what the runtime exposes at the cursor
//The server builds the same environment a task run gets, so a name in the list is one that really exists
function requestScriptCompletion(editor) {
    if (!editor) {
        return;
    }

    if (!vscode || !vscode.postMessage) {
        showStaticScriptHints(editor);
        return;
    }

    const cursor = editor.getCursor();
    const request = ++scriptHintRequest;
    scriptHintPending = { request, line: cursor.line, ch: cursor.ch };

    vscode.postMessage({
        type: "scriptComplete",
        id: request,
        source: editor.getValue(),
        line: cursor.line + 1,
        column: cursor.ch + 1,
    });
}

//applyScriptComplete opens the list once the server answered, or falls back to the built-in names
function applyScriptComplete(message) {
    if (!scriptHintPending || message.id !== scriptHintPending.request || !taskScriptEditor) {
        return;
    }

    const pending = scriptHintPending;
    scriptHintPending = null;

    if (!message.ok || !message.items || message.items.length === 0) {
        showStaticScriptHints(taskScriptEditor);
        return;
    }

    const cursor = taskScriptEditor.getCursor();
    if (cursor.line !== pending.line || cursor.ch !== pending.ch) {
        return;
    }

    scriptHintState = { items: message.items };
    CodeMirror.simpleHint(taskScriptEditor, scriptHintsFromServer, {
        completeSingle: false,
    });
}

//scriptHintsFromServer filters the names the server returned by what was typed since then
function scriptHintsFromServer(editor) {
    const cursor = editor.getCursor();
    const line = editor.getLine(cursor.line).slice(0, cursor.ch);
    const match = line.match(/[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*\.?$/);
    const typed = match ? match[0] : "";
    const prefix = typed.endsWith(".") ? "" : typed.slice(typed.lastIndexOf(".") + 1);
    const names = (scriptHintState ? scriptHintState.items : [])
        .map((item) => item.name)
        .filter((name) => name.startsWith(prefix));

    if (names.length === 0) {
        return null;
    }

    return {
        list: names,
        from: CodeMirror.Pos(cursor.line, cursor.ch - prefix.length),
        to: CodeMirror.Pos(cursor.line, cursor.ch),
    };
}

//scheduleScriptCheck parses the script after typing stops, since every keystroke would rebuild the parser
function scheduleScriptCheck(editor) {
    if (scriptCheckTimer) {
        clearTimeout(scriptCheckTimer);
    }

    scriptCheckTimer = setTimeout(() => {
        scriptCheckTimer = 0;
        if (!editor || !vscode || !vscode.postMessage) {
            return;
        }

        vscode.postMessage({
            type: "scriptCheck",
            id: ++scriptCheckRequest,
            source: editor.getValue(),
        });
    }, 400);
}

//applyScriptCheck shows the first syntax error under the hint, or clears it
function applyScriptCheck(message) {
    if (message.id !== scriptCheckRequest) {
        return;
    }

    if (message.diagnostic) {
        setTaskScriptStatus(
            `${message.diagnostic.line}:${message.diagnostic.column} ${message.diagnostic.message}`
        );
        return;
    }

    setTaskScriptStatus("");
}

function setTaskScriptStatus(text) {
    const status = document.getElementById("task-script-status");
    if (status) {
        status.textContent = text || "";
    }
}

//SCRIPT_API every name the script runtime hands out, used when the server cannot answer
const SCRIPT_API = [
    "log.info(", "log.warn(", "log.error(",
    "project.dir", "project.path", "project.name", "project.modId", "project.modVersion",
    "project.configuration", "project.build.assemblyName", "project.build.output",
    "project.build.outputType", "project.build.nullable", "project.build.allowUnsafe",
    "project.build.langVersion", "project.tasks", "project.packages", "project.sources",
    "project.manifest",
    "fs.read(", "fs.write(", "fs.exists(", "fs.glob(", "fs.list(", "fs.mkdir(",
    "fs.remove(", "fs.copy(",
    "path.join(", "path.dirname(", "path.basename(", "path.extname(", "path.absolute(",
    "path.relative(",
    "env.get(", "env.names(",
    "hash.sha256(", "hash.sha256File(",
    "xml.select(", "xml.root(",
    "syntax.query(", "syntax.walk(",
];

//showStaticScriptHints lists the built-in names when the server did not answer
function showStaticScriptHints(editor) {
    if (!editor || !CodeMirror.simpleHint) {
        return;
    }

    CodeMirror.simpleHint(editor, scriptHints, { completeSingle: false });
}

//scriptHints lists the names that start with what was typed so far
function scriptHints(editor) {
    const cursor = editor.getCursor();
    const line = editor.getLine(cursor.line).slice(0, cursor.ch);
    const match = line.match(/[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*\.?$/);
    const prefix = match ? match[0] : "";
    const list = SCRIPT_API.filter((name) => name.startsWith(prefix));
    return {
        list: prefix.length > 0 ? list : SCRIPT_API,
        from: CodeMirror.Pos(cursor.line, cursor.ch - prefix.length),
        to: CodeMirror.Pos(cursor.line, cursor.ch),
    };
}

//initTextMate loads the editor's own xml grammar, so the page shows exactly the scopes VS Code does
//The plain xml mode stays in place whenever the grammar or the wasm cannot be loaded
async function initTextMate() {
    if (!window.NcTextMate || !window.__nacrecraftXmlGrammar) {
        return;
    }

    try {
        const wasm = await fetch(window.__nacrecraftOnigWasm).then((response) =>
            response.arrayBuffer()
        );
        await window.NcTextMate.loadWASM(wasm);

        const registry = new window.NcTextMate.Registry({
            onigLib: Promise.resolve({
                createOnigScanner: (patterns) =>
                    new window.NcTextMate.OnigScanner(patterns),
                createOnigString: (value) => new window.NcTextMate.OnigString(value),
            }),
            loadGrammar: async () => window.__nacrecraftXmlGrammar,
        });

        const grammar = await registry.loadGrammar("text.xml");
        if (!grammar) {
            return;
        }

        tmGrammar = grammar;
        defineTextMateMode();

        if (taskEditor) {
            taskEditor.setOption("mode", "ncxml-tm");
            tokenizeTextMate(taskEditor);
        }
    } catch (error) {
        console.warn("textmate unavailable", error);
    }
}

//defineTextMateMode turns the token table into a code mirror mode
function defineTextMateMode() {
    CodeMirror.defineMode("ncxml-tm", function () {
        return {
            token: function (stream) {
                const ranges = tmLines[stream.lineOracle.line];
                if (ranges) {
                    const pos = stream.pos;
                    for (const range of ranges) {
                        if (pos >= range.from && pos < range.to) {
                            stream.pos = range.to;
                            return range.cls;
                        }
                    }
                }

                stream.next();
                return null;
            },
        };
    });
}

//scheduleTextMate retokenizes after typing settles, which keeps every keystroke cheap
function scheduleTextMate(cm) {
    if (!tmGrammar) {
        return;
    }

    clearTimeout(tmTimer);
    tmTimer = setTimeout(() => tokenizeTextMate(cm), 40);
}

//tokenizeTextMate asks the grammar for one token table per line
function tokenizeTextMate(cm) {
    if (!tmGrammar) {
        return;
    }

    const result = [];
    let stack = null;
    for (const line of cm.getValue().split("\n")) {
        const tokens = tmGrammar.tokenizeLine(line, stack);
        stack = tokens.ruleStack;

        const ranges = [];
        for (let index = 0; index < tokens.tokens.length; index++) {
            const token = tokens.tokens[index];
            const end =
                index + 1 < tokens.tokens.length
                    ? tokens.tokens[index + 1].startIndex
                    : line.length;
            if (end <= token.startIndex) {
                continue;
            }

            ranges.push({
                from: token.startIndex,
                to: end,
                cls: scopeClass(token.scopes),
            });
        }

        result.push(ranges);
    }

    tmLines = result;
    cm.refresh();
}

//scopeClass maps one textmate scope stack onto the class the stylesheet colors
function scopeClass(scopes) {
    const text = scopes.join(" ");
    if (text.indexOf("comment") >= 0) {
        return "tm-comment";
    }
    if (text.indexOf("string") >= 0) {
        return "tm-string";
    }
    if (text.indexOf("constant.character.entity") >= 0) {
        return "tm-entity";
    }
    if (text.indexOf("entity.other.attribute-name") >= 0) {
        return "tm-attribute";
    }
    if (text.indexOf("punctuation.definition.tag") >= 0) {
        return "tm-punctuation";
    }
    if (text.indexOf("entity.name.tag") >= 0) {
        return "tm-tag";
    }
    if (text.indexOf("meta.tag") >= 0) {
        return "tm-meta";
    }

    return null;
}

//setTaskTab switches between the block editor and the raw xml
//Moving to the xml side writes the blocks out, moving back parses what was typed
function setTaskTab(tab, force = false) {
    if (tab === taskTab && !force) {
        return;
    }

    if (force) {
        if (tab === "visual") {
            renderTaskVisual();
        }
    } else if (tab === "xml") {
        taskEditor.setValue(buildTaskXml(taskModel));
    } else {
        try {
            taskModel = parseTaskXml(taskEditor.getValue());
        } catch (error) {
            setTaskEditorStatus(String(error));
            return;
        }
        renderTaskVisual();
    }

    taskTab = tab;
    document.querySelectorAll(".task-detail-tab").forEach((node) => {
        node.classList.toggle("active", node.dataset.ttab === tab);
    });
    document.getElementById("task-visual").classList.toggle("hidden", tab !== "visual");
    document.getElementById("task-xml-pane").classList.toggle("hidden", tab !== "xml");

    if (tab === "xml") {
        setTimeout(() => taskEditor.refresh(), 0);
    }
}

//renderTaskVisual writes the task fields and one block per step
function renderTaskVisual() {
    if (!taskModel) {
        return;
    }

    const fields = document.getElementById("task-fields");
    fields.textContent = "";
    fields.append(
        taskField("tasks.field.name", taskModel.name, (value) => {
            taskModel.name = value;
        }),
        taskField("tasks.field.description", taskModel.description, (value) => {
            taskModel.description = value;
        }),
        taskField("tasks.field.depends", taskModel.depends, (value) => {
            taskModel.depends = value;
        })
    );

    const check = document.createElement("label");
    check.className = "task-field task-field-check";
    const checkText = document.createElement("span");
    checkText.textContent = gui.texts["tasks.field.override"];
    const box = document.createElement("input");
    box.type = "checkbox";
    box.checked = taskModel.override;
    box.addEventListener("change", () => {
        taskModel.override = box.checked;
    });
    check.append(checkText, box);
    fields.append(check);

    const steps = document.getElementById("task-steps");
    steps.textContent = "";
    if (taskModel.steps.length === 0) {
        const note = document.createElement("div");
        note.className = "task-step-empty";
        note.textContent = gui.texts["tasks.noSteps"];
        steps.append(note);
        return;
    }

    taskModel.steps.forEach((step, index) => {
        steps.append(taskStep(step, index));
    });
}

//taskField builds one labelled input, which writes straight into the model
function taskField(labelKey, value, onChange) {
    const row = document.createElement("label");
    row.className = "task-field";

    const name = document.createElement("span");
    name.textContent = gui.texts[labelKey] || labelKey;

    const input = document.createElement("input");
    input.value = value || "";
    input.spellcheck = false;
    input.addEventListener("input", () => onChange(input.value));

    row.append(name, input);
    return row;
}

//taskStep builds one step block with its order arrows and its own fields
function taskStep(step, index) {
    const block = document.createElement("div");
    block.className = "task-step";

    const head = document.createElement("div");
    head.className = "task-step-head";

    const kind = document.createElement("span");
    kind.className = "task-step-kind";
    kind.textContent = stepLabel(step.kind);

    const order = document.createElement("div");
    order.className = "task-step-order";

    const up = document.createElement("button");
    up.className = "step-move step-up";
    up.title = gui.texts["tasks.moveUp"];
    up.disabled = index === 0;
    up.addEventListener("click", () => moveStep(index, -1));

    const down = document.createElement("button");
    down.className = "step-move step-down";
    down.title = gui.texts["tasks.moveDown"];
    down.disabled = index === taskModel.steps.length - 1;
    down.addEventListener("click", () => moveStep(index, 1));

    order.append(up, down);

    const remove = document.createElement("button");
    remove.className = "task-step-remove";
    remove.textContent = "\u2715";
    remove.title = gui.texts["tasks.removeStep"];
    remove.addEventListener("click", () => removeStep(index));

    head.append(kind, order, remove);
    block.append(head);

    const body = document.createElement("div");
    body.className = "task-step-fields";

    if (step.kind === "exec") {
        body.append(
            stepField("tasks.field.command", step.command, (value) => {
                step.command = value;
            })
        );
    } else if (step.kind === "copy" || step.kind === "zip") {
        body.append(
            stepField("tasks.field.from", step.from, (value) => {
                step.from = value;
            }),
            stepField("tasks.field.to", step.to, (value) => {
                step.to = value;
            })
        );
    } else if (step.kind === "tool") {
        body.append(
            stepField("tasks.field.tool", step.name, (value) => {
                step.name = value;
            }),
            stepField("tasks.field.method", step.method, (value) => {
                step.method = value;
            }),
            stepField("tasks.field.args", step.args, (value) => {
                step.args = value;
            })
        );
    } else {
        const raw = document.createElement("pre");
        raw.className = "task-step-raw";
        raw.textContent = step.xml;
        body.append(raw);
    }

    block.append(body);
    return block;
}

//stepField builds one input inside a step block
function stepField(labelKey, value, onChange) {
    const row = document.createElement("label");
    row.className = "task-field";

    const name = document.createElement("span");
    name.textContent = gui.texts[labelKey] || labelKey;

    const input = document.createElement("input");
    input.value = value || "";
    input.spellcheck = false;
    input.addEventListener("input", () => onChange(input.value));

    row.append(name, input);
    return row;
}

//stepLabel names the kind of a step
function stepLabel(kind) {
    if (kind === "raw") {
        return "XML";
    }

    return kind.charAt(0).toUpperCase() + kind.slice(1);
}

//moveStep swaps one step with its neighbour
function moveStep(index, delta) {
    const target = index + delta;
    if (target < 0 || target >= taskModel.steps.length) {
        return;
    }

    const moving = taskModel.steps.splice(index, 1)[0];
    taskModel.steps.splice(target, 0, moving);
    renderTaskVisual();
}

//removeStep drops one step from the task
function removeStep(index) {
    taskModel.steps.splice(index, 1);
    renderTaskVisual();
}

//parseTaskXml turns one task element into the block model
function parseTaskXml(xml) {
    const source = (xml || "").trim();
    if (source.length === 0) {
        throw new Error(gui.texts["tasks.xmlEmpty"]);
    }

    const parsed = new DOMParser().parseFromString(source, "application/xml");
    if (parsed.getElementsByTagName("parsererror").length > 0) {
        throw new Error(gui.texts["tasks.xmlInvalid"]);
    }

    const task = parsed.documentElement;
    if (!task || task.localName.toLowerCase() !== "task") {
        throw new Error(gui.texts["tasks.xmlRoot"]);
    }

    const model = {
        name: task.getAttribute("Name") || "",
        description: task.getAttribute("Description") || "",
        depends: task.getAttribute("Depends") || "",
        override: (task.getAttribute("Override") || "").toLowerCase() === "true",
        steps: [],
    };

    for (const child of task.children) {
        const kind = child.localName.toLowerCase();
        if (kind === "exec") {
            model.steps.push({ kind: "exec", command: child.textContent.trim() });
        } else if (kind === "copy" || kind === "zip") {
            model.steps.push({
                kind,
                from: child.getAttribute("From") || "",
                to: child.getAttribute("To") || "",
            });
        } else if (kind === "tool") {
            model.steps.push({
                kind: "tool",
                name: child.getAttribute("Name") || "",
                method: child.getAttribute("Method") || "",
                args: child.getAttribute("Args") || "",
            });
        } else {
            model.steps.push({
                kind: "raw",
                xml: new XMLSerializer().serializeToString(child),
            });
        }
    }

    return model;
}

//buildTaskXml writes the block model back as one task element
function buildTaskXml(model) {
    const attributes = ['Name="' + attribute(model.name) + '"'];
    if (model.description.length > 0) {
        attributes.push('Description="' + attribute(model.description) + '"');
    }
    if (model.depends.trim().length > 0) {
        attributes.push('Depends="' + attribute(model.depends) + '"');
    }
    if (model.override) {
        attributes.push('Override="true"');
    }

    const lines = ["<Task " + attributes.join(" ") + ">"];
    for (const step of model.steps) {
        if (step.kind === "exec") {
            lines.push("  <Exec>" + text(step.command) + "</Exec>");
        } else if (step.kind === "copy" || step.kind === "zip") {
            lines.push(
                "  <" +
                    stepLabel(step.kind) +
                    ' From="' +
                    attribute(step.from) +
                    '" To="' +
                    attribute(step.to) +
                    '" />'
            );
        } else if (step.kind === "tool") {
            let line =
                "  <Tool Name=\"" +
                attribute(step.name) +
                '" Method="' +
                attribute(step.method) +
                '"';
            if (step.args.length > 0) {
                line += ' Args="' + attribute(step.args) + '"';
            }
            lines.push(line + " />");
        } else {
            lines.push("  " + step.xml.replace(/\r?\n/g, "\n  "));
        }
    }
    lines.push("</Task>");
    return lines.join("\n");
}

//buildScriptTaskXml writes the task header and one script step
function buildScriptTaskXml(model, source) {
    const attributes = ['Name="' + attribute(model.name) + '"'];
    if (model.description.length > 0) {
        attributes.push('Description="' + attribute(model.description) + '"');
    }
    if (model.depends.trim().length > 0) {
        attributes.push('Depends="' + attribute(model.depends) + '"');
    }
    if (model.override) {
        attributes.push('Override="true"');
    }

    //a cdata section cannot carry its own terminator, so one in the source is split
    const body = String(source || "").replace(/]]>/g, "]]]]><![CDATA[>");
    return (
        "<Task " +
        attributes.join(" ") +
        ">\n  <Script><![CDATA[" +
        body +
        "]]></Script>\n</Task>"
    );
}

//attribute escapes one attribute value
function attribute(value) {
    return String(value || "")
        .replace(/&/g, "&amp;")
        .replace(/"/g, "&quot;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

//text escapes one element body
function text(value) {
    return String(value || "")
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
}

//emptyTask is the model used when the stored xml cannot be parsed
function emptyTask(name) {
    return {
        name: name,
        description: "",
        depends: "",
        override: false,
        steps: [],
    };
}

//newTaskName picks a name no declared task uses yet
function newTaskName() {
    const names = new Set(taskEntries.map((task) => task.name));
    let index = 1;
    while (names.has("task" + index)) {
        index++;
    }
    return "task" + index;
}

//addBlock appends one step of the chosen kind
function addBlock(kind) {
    const step = { kind };
    if (kind === "exec") {
        step.command = "echo hello";
    } else if (kind === "copy" || kind === "zip") {
        step.from = "";
        step.to = "";
    } else {
        step.kind = "tool";
        step.name = "";
        step.method = "";
        step.args = "";
    }

    taskModel.steps.push(step);
    renderTaskVisual();
}

//setTaskEditorStatus shows a parse or save problem beside the buttons
function setTaskEditorStatus(text) {
    document.getElementById("task-editor-status").textContent = text || "";
}

//saveTask sends the task, writing it from the active tab
function saveTask() {
    if (!taskModel) {
        return;
    }

    let xml;
    if (taskDetailMode === "script") {
        xml = buildScriptTaskXml(
            taskModel,
            taskScriptEditor ? taskScriptEditor.getValue() : taskScriptSource
        );
    } else if (taskTab === "xml") {
        xml = taskEditor.getValue();
    } else {
        xml = buildTaskXml(taskModel);
    }

    taskSaving = true;
    setTaskEditorStatus(gui.texts["tasks.saving"]);
    vscode.postMessage({
        type: "tasksSaveTask",
        name: taskEditingName,
        config: taskEditingConfig,
        xml,
    });
}

//renderTaskTexts redraws everything a language change translates
function renderTaskTexts() {
    drawTasks();
    if (taskModel) {
        renderTaskVisual();
    }
    if (taskEditor) {
        setTaskEditorStatus(taskSaving ? gui.texts["tasks.saving"] : "");
    }
    if (taskEditingName.length > 0) {
        document.getElementById("task-detail-title").textContent = taskEditingName;
    }
}

document.getElementById("tasks-add").addEventListener("click", createTask);
document.getElementById("tasks-add-script").addEventListener("click", createScriptTask);
document.getElementById("tasks-delete").addEventListener("click", deleteTask);
document.getElementById("tasks-save").addEventListener("click", saveTask);
document.getElementById("tasks-cancel").addEventListener("click", closeTaskDetail);
document.getElementById("tasks-override").addEventListener("change", (event) => {
    const enabled = event.target.checked;
    taskOverrides = enabled;
    taskError = "";
    taskNotice = "";
    drawTasks();
    vscode.postMessage({ type: "tasksOverride", enabled });
});
document.querySelectorAll(".task-detail-tab").forEach((node) => {
    node.addEventListener("click", () => setTaskTab(node.dataset.ttab));
});
document.querySelectorAll("[data-block]").forEach((node) => {
    node.addEventListener("click", () => addBlock(node.dataset.block));
});

//dragging the splitter sizes the side pane, which starts at 400 and never eats the list
document.getElementById("tasks-split").addEventListener("mousedown", (event) => {
    taskResizing = true;
    document.getElementById("task-detail").classList.add("resizing");
    event.preventDefault();
});
window.addEventListener("mousemove", (event) => {
    if (!taskResizing) {
        return;
    }

    const body = document.querySelector(".tasks-body");
    const right = body.getBoundingClientRect().right;
    const width = Math.min(Math.max(right - event.clientX, 240), 900);
    document.documentElement.style.setProperty(
        "--task-detail-width",
        width + "px"
    );
});
window.addEventListener("mouseup", () => {
    if (!taskResizing) {
        return;
    }

    taskResizing = false;
    document.getElementById("task-detail").classList.remove("resizing");
});

//the grammar comes from the editor itself, so the page picks it up as soon as the wasm is ready
void initTextMate();
