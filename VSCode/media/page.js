"use strict";

//The main page script
//It owns the shared state and the message routing; the assembly and package pages live in their own files

const dialog = document.getElementById("dialog");
const status = document.getElementById("dialog-status");
const confirm = document.getElementById("confirm");
let project = null;
let iconUri = null;
let diagnostics = [];
let lastStatus = "";
let lastEntries = [];

window.addEventListener("message", (event) => {
    const message = event.data;
    if (message.type === "texts") {
        gui.setTexts(message.texts);
        renderProject();
        renderRules();
        renderDiagnostics();
        renderAsmTexts();
        renderNugetTexts();
        renderNuget();
        renderConfigTexts();
        renderTaskTexts();
        //the log tail carries translated text of its own, the status line keeps the last operation
        renderLog(lastStatus, lastEntries);
        return;
    }
    if (message.type === "settings") {
        renderSettings(message.items);
        applyNugetSources(message.items);
        applyNugetSize(message.items);
        applyLogHeight(message.items);
        return;
    }
    if (message.type === "project") {
        project = message.project;
        iconUri = message.iconUri;
        //another project has other references, the package page starts over
        const key = project ? project.root : "";
        if (key !== nugetProjectKey) {
            nugetProjectKey = key;
            nugetRefs = null;
            nugetRefsError = "";
            nugetResults = [];
            nugetSearched = false;
            nugetKernelOpen = false;
            nugetSelected = "";
            if (!document.getElementById("page-nuget").classList.contains("hidden")) {
                loadReferences();
            } else {
                renderNuget();
            }
        }
        renderProject();
        renderRules();
        return;
    }
    if (message.type === "diagnostics") {
        diagnostics = message.items || [];
        renderDiagnostics();
        return;
    }
    if (message.type === "openCreate") {
        openDialog();
        return;
    }
    if (message.type === "log") {
        renderLog(message.status, message.entries);
        return;
    }
    if (message.type === "creating") {
        status.textContent = gui.texts["create.working"];
        confirm.disabled = true;
        return;
    }
    if (message.type === "created") {
        confirm.disabled = false;
        status.textContent = message.ok ? "" : gui.texts["create.failed"];
        if (message.ok) {
            dialog.classList.add("hidden");
        }
        return;
    }
    if (message.type === "asmResult") {
        const resolve = asmPending.get(message.id);
        asmPending.delete(message.id);
        if (resolve) {
            resolve(message);
        }
        return;
    }
    if (
        message.type === "nugetRefs" ||
        message.type === "nugetSet" ||
        message.type === "nugetSearch" ||
        message.type === "nugetReadme" ||
        message.type === "nugetVersions"
    ) {
        const resolve = nugetPending.get(message.id);
        nugetPending.delete(message.id);
        if (resolve) {
            resolve(message);
        }
        return;
    }
    if (message.type === "asmFile") {
        document.getElementById("asm-file").value = message.file;
        loadAssembly();
        return;
    }
    if (message.type === "asmDefault") {
        const input = document.getElementById("asm-file");
        if (input.value.length === 0) {
            input.value = message.file;
            loadAssembly();
        }
        return;
    }
    if (message.type === "asmEdited") {
        setAsmStatus(message.text || "");
        if (message.ok) {
            loadResources();
        }
        return;
    }
    if (message.type === "config" || message.type === "configSet") {
        applyConfig(message);
        return;
    }
    if (message.type === "tasks") {
        applyTasks(message);
        return;
    }
    if (message.type === "taskStarted") {
        applyTaskStarted(message);
        return;
    }
    if (message.type === "taskFinished") {
        applyTaskFinished(message);
        return;
    }
    if (message.type === "scriptComplete") {
        applyScriptComplete(message);
        return;
    }
    if (message.type === "scriptCheck") {
        applyScriptCheck(message);
    }
});

document.querySelectorAll(".nav-item").forEach((item) => {
    item.addEventListener("click", () => select(item));
});

document.getElementById("open-json").addEventListener("click", () => {
    vscode.postMessage({ type: "openJson" });
});

document.getElementById("add-project").addEventListener("click", () => {
    vscode.postMessage({ type: "addRef", kind: "project" });
});

document.getElementById("add-assembly").addEventListener("click", () => {
    vscode.postMessage({ type: "addRef", kind: "assembly" });
});

document.getElementById("asm-load").addEventListener("click", loadAssembly);
document.getElementById("asm-browse").addEventListener("click", () => {
    vscode.postMessage({ type: "asmBrowse" });
});
document.getElementById("asm-file").addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
        loadAssembly();
    }
});
document.getElementById("asm-filter").addEventListener("input", renderTypes);
document.getElementById("asm-add").addEventListener("click", () => {
    vscode.postMessage({ type: "asmEmbed", file: asmFile });
});
document.querySelectorAll(".asm-tab").forEach((node) => {
    node.addEventListener("click", () => showAsmTab(node.dataset.tab));
});

document.querySelectorAll(".nuget-tab").forEach((node) => {
    node.addEventListener("click", () => selectNugetTab(node.dataset.ntab));
});
document.getElementById("nuget-search").addEventListener("click", () => searchPackages(false));
document.getElementById("nuget-query").addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
        searchPackages(false);
    }
});
document.getElementById("nuget-source").addEventListener("change", () => {
    if (nugetSearched) {
        searchPackages(false);
    }
});
//the refresh asks the feed again and stores the answer, which the cached listing then reads
document.getElementById("nuget-refresh").addEventListener("click", () => {
    for (const id of Object.keys(nugetDescriptions)) {
        delete nugetDescriptions[id];
    }
    searchPackages(true);
});

//dragging the splitter resizes the description pane, which starts at 440 and never eats the list
let nugetResizing = false;
document.getElementById("nuget-split").addEventListener("mousedown", (event) => {
    nugetResizing = true;
    document.getElementById("nuget-detail").classList.add("resizing");
    event.preventDefault();
});
window.addEventListener("mousemove", (event) => {
    if (!nugetResizing) {
        return;
    }

    const body = document.querySelector(".nuget-body");
    const right = body.getBoundingClientRect().right;
    const width = Math.min(Math.max(right - event.clientX, 260), 900);
    document.documentElement.style.setProperty(
        "--nuget-detail-width",
        width + "px"
    );
});
window.addEventListener("mouseup", () => {
    if (!nugetResizing) {
        return;
    }

    nugetResizing = false;
    document.getElementById("nuget-detail").classList.remove("resizing");
});

//the log height in the settings is the live value, so a drag writes it back
function applyLogHeight(items) {
    const item = items.find((entry) => entry.key === "log.height");
    const value = parseInt(String(item ? item.value : ""), 10);
    setLogHeight(isNaN(value) ? 120 : value);
}

function setLogHeight(value) {
    const max = Math.max(25, window.innerHeight - 220);
    const height = Math.min(Math.max(Math.round(value), 25), max);
    document.documentElement.style.setProperty("--log-height", height + "px");
}

let logResizing = false;
document.getElementById("log-resize").addEventListener("mousedown", (event) => {
    logResizing = true;
    event.preventDefault();
});
window.addEventListener("mousemove", (event) => {
    if (!logResizing) {
        return;
    }

    const log = document.getElementById("log");
    setLogHeight(log.getBoundingClientRect().bottom - event.clientY);
});
window.addEventListener("mouseup", () => {
    if (!logResizing) {
        return;
    }

    logResizing = false;
    const log = document.getElementById("log");
    vscode.postMessage({
        type: "update",
        key: "log.height",
        value: String(Math.round(log.getBoundingClientRect().height)),
    });
});

document.getElementById("create").addEventListener("click", openDialog);
document.getElementById("cancel").addEventListener("click", () => {
    dialog.classList.add("hidden");
});
confirm.addEventListener("click", submit);

function select(item) {
    document.querySelectorAll(".nav-item").forEach((node) => {
        node.classList.toggle("active", node === item);
    });
    document.querySelectorAll(".page").forEach((node) => {
        node.classList.add("hidden");
    });
    const page = item.dataset.page;
    document.getElementById("page-" + page).classList.remove("hidden");

    //the references are read the first time the package page is opened, not at startup
    if (page === "nuget") {
        ensureNuget();
        //the page is wide, the extension closes both side bars for it unless the setting says otherwise
        vscode.postMessage({ type: "nugetFocus" });
    }

    //the settings are read every time the page opens, since the config file changes outside the editor too
    if (page === "config") {
        loadConfig();
    }

    //the task block is read on every visit as well, since the project file is edited outside the page
    if (page === "tasks") {
        loadTasks();
    }
}

function renderProject() {
    const card = document.getElementById("home-project");
    const empty = document.getElementById("home-empty");
    const icon = document.getElementById("home-icon");

    if (!project) {
        //not a mod yet: the home page keeps the rest of the page usable and only offers the creation entry
        card.classList.add("hidden");
        empty.classList.remove("hidden");
        return;
    }

    empty.classList.add("hidden");
    card.classList.remove("hidden");

    if (iconUri) {
        icon.src = iconUri;
        icon.classList.remove("hidden");
    } else {
        icon.removeAttribute("src");
        icon.classList.add("hidden");
    }

    document.getElementById("home-name").textContent = project.name;
    document.getElementById("home-meta").textContent =
        project.id + "  " + project.version;

    const rows = [
        ["project.description", project.description],
        ["project.environment", project.environment],
        ["project.entry", project.entry],
        ["project.root", project.root],
    ];

    const body = document.getElementById("home-card");
    body.textContent = "";

    for (const pair of rows) {
        const value = pair[1] || "";
        if (value.length === 0) {
            continue;
        }

        const name = document.createElement("span");
        name.className = "card-key";
        name.textContent = gui.texts[pair[0]] || pair[0];

        const content = document.createElement("span");
        content.className = "card-value";
        content.textContent = value;
        content.title = value;

        body.append(name, content);
    }
}

function renderRules() {
    const list = document.getElementById("rule-list");
    const empty = document.getElementById("rule-empty");
    list.textContent = "";

    const hooks = project ? project.hooks : [];
    const mixins = project ? project.mixins : [];
    empty.classList.toggle("hidden", hooks.length + mixins.length > 0);

    for (const hook of hooks) {
        const row = document.createElement("div");
        row.className = "rule";

        const head = document.createElement("span");
        head.className = "rule-head";
        head.textContent = hook.type + "  " + hook.target + "::" + hook.method;
        head.title = head.textContent;

        const body = document.createElement("span");
        body.className = "rule-body";
        body.textContent = "-> " + hook.replaceType + "." + hook.replaceMethod;
        body.title = body.textContent;

        row.append(head, body);
        list.append(row);
    }

    for (const mixin of mixins) {
        const row = document.createElement("div");
        row.className = "rule";

        const head = document.createElement("span");
        head.className = "rule-head";
        head.textContent = "Mixin  " + mixin.target;

        const body = document.createElement("span");
        body.className = "rule-body";
        body.textContent = "<- " + mixin.source;

        row.append(head, body);
        list.append(row);
    }
}

function renderDiagnostics() {
    const list = document.getElementById("diagnostic-list");
    const empty = document.getElementById("diagnostic-empty");
    list.textContent = "";
    empty.classList.toggle("hidden", diagnostics.length > 0);

    for (const item of diagnostics) {
        const row = document.createElement("div");
        row.className = "diag diag-" + item.severity;

        const severity = document.createElement("span");
        severity.className = "diag-severity";
        severity.textContent =
            gui.texts["diagnostics." + item.severity] || item.severity;

        const body = document.createElement("div");
        body.className = "diag-body";

        const lines = item.message.split("\n");
        const message = document.createElement("span");
        message.className = "diag-message";
        message.textContent = lines[0];
        body.append(message);

        if (lines.length > 1) {
            const help = document.createElement("span");
            help.className = "diag-help";
            help.textContent = lines.slice(1).join("\n");
            body.append(help);
        }

        const where = document.createElement("span");
        where.className = "diag-location";
        where.textContent =
            item.display + ":" + item.line + ":" + item.column;

        row.append(severity, body, where);
        row.addEventListener("click", () => {
            vscode.postMessage({
                type: "openDiagnostic",
                file: item.file,
                line: item.line,
                column: item.column,
            });
        });
        list.append(row);
    }
}

function openDialog() {
    dialog.classList.remove("hidden");
    status.textContent = "";
    confirm.disabled = false;
    document.getElementById("f-name").focus();
}

function submit() {
    const values = {
        name: value("f-name"),
        id: value("f-id"),
        description: value("f-description"),
        authors: value("f-authors"),
        homepage: value("f-homepage"),
        sources: value("f-sources"),
        license: value("f-license"),
        backup: document.getElementById("f-backup").checked,
    };

    if (values.name.length === 0) {
        status.textContent = gui.texts["create.nameRequired"];
        return;
    }

    vscode.postMessage({ type: "create", values });
}

function value(id) {
    return document.getElementById(id).value.trim();
}

//the tool colors its output for a terminal, so its escapes become styled spans instead of showing up as raw text
const ansiPattern = /\u001b\[([0-9;]*)m/g;
const ansiColors = {
    "30": "--vscode-terminal-ansiBlack",
    "31": "--vscode-terminal-ansiRed",
    "32": "--vscode-terminal-ansiGreen",
    "33": "--vscode-terminal-ansiYellow",
    "34": "--vscode-terminal-ansiBlue",
    "35": "--vscode-terminal-ansiMagenta",
    "36": "--vscode-terminal-ansiCyan",
    "37": "--vscode-terminal-ansiWhite",
    "90": "--vscode-terminal-ansiBrightBlack",
    "91": "--vscode-terminal-ansiBrightRed",
    "92": "--vscode-terminal-ansiBrightGreen",
    "93": "--vscode-terminal-ansiBrightYellow",
    "94": "--vscode-terminal-ansiBrightBlue",
    "95": "--vscode-terminal-ansiBrightMagenta",
    "96": "--vscode-terminal-ansiBrightCyan",
    "97": "--vscode-terminal-ansiBrightWhite"
};

function renderAnsi(container, value) {
    container.textContent = "";
    let style = {};
    let last = 0;
    let match;

    const flush = (piece) => {
        if (piece.length === 0) {
            return;
        }

        const node = document.createElement("span");
        node.textContent = piece;
        if (style.color) {
            node.style.color = style.color;
        }
        if (style.bold) {
            node.style.fontWeight = "bold";
        }
        container.appendChild(node);
    };

    ansiPattern.lastIndex = 0;
    while ((match = ansiPattern.exec(value)) !== null) {
        flush(value.slice(last, match.index));
        last = ansiPattern.lastIndex;

        for (const code of match[1].split(";")) {
            if (code === "" || code === "0") {
                style = {};
            } else if (code === "1") {
                style.bold = true;
            } else if (code === "22") {
                style.bold = false;
            } else if (code === "39") {
                style.color = "";
            } else if (ansiColors[code]) {
                style.color = "var(" + ansiColors[code] + ")";
            }
        }
    }

    flush(value.slice(last));
}

function renderLog(status, entries) {
    lastStatus = status || "";
    lastEntries = entries || [];

    const line = document.getElementById("status-line");
    //an operation in flight replaces the resting text, the log keeps the history
    line.textContent =
        lastStatus.length > 0
            ? lastStatus
            : gui.texts["status.ready"] || "ready";

    const list = document.getElementById("log-list");
    list.textContent = "";
    for (const entry of lastEntries) {
        const row = document.createElement("div");
        row.className = "log-line log-" + entry.level;

        const time = document.createElement("span");
        time.className = "log-time";
        time.textContent = entry.time;

        const text = document.createElement("span");
        renderAnsi(text, entry.message);

        row.append(time, text);
        list.append(row);
    }
    list.scrollTop = list.scrollHeight;
}

function renderSettings(items) {
    const list = document.getElementById("setting-list");
    list.textContent = "";
    for (const item of items) {
        const row = document.createElement("div");
        row.className = "setting";

        const name = document.createElement("span");
        name.className = "setting-name";
        name.textContent = item.label;
        name.title = item.description;

        const value = document.createElement("span");
        value.className = "setting-value";
        value.textContent = item.value === "" ? gui.texts["settings.empty"] : String(item.value);

        row.append(name, value);
        row.addEventListener("click", () => edit(row, item));
        list.append(row);
    }
}

function edit(row, item) {
    if (row.querySelector(".editor")) {
        return;
    }

    const wrapper = document.createElement("span");
    wrapper.className = "editor";

    let input;
    if (item.options.length > 0) {
        input = document.createElement("select");
        for (const option of item.options) {
            const node = document.createElement("option");
            node.value = String(option);
            const label =
                option === ""
                    ? gui.texts["settings.followEditor"]
                    : gui.texts["settings.option." + option] || String(option);
            node.textContent = label;
            if (String(item.value) === String(option)) {
                node.selected = true;
            }
            input.append(node);
        }
    } else {
        input = document.createElement("input");
        input.value = String(item.value ?? "");
    }

    const save = document.createElement("button");
    save.textContent = gui.texts["settings.save"];
    save.addEventListener("click", (event) => {
        event.stopPropagation();
        vscode.postMessage({ type: "update", key: item.key, value: input.value });
    });

    wrapper.append(input, save);
    row.append(wrapper);
}

gui.setTexts(window.__nacrecraftText);
renderProject();
renderRules();
renderDiagnostics();
renderNugetTexts();
vscode.postMessage({ type: "ready" });
