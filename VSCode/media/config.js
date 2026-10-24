"use strict";

//The configuration page
//It lists every setting the project config may declare and asks the language server to write the ones the reader picks
//The server owns the file, so this only draws what came back and sends the changes

let configEntries = [];
let configEditing = "";
let configAdded = "";
let configPending = "";
let configGeneration = 0;

//loadConfig asks the server for the full list, which also refreshes the values
function loadConfig() {
    configPending = "";
    configEditing = "";
    vscode.postMessage({ type: "config" });
}

//applyConfig draws the answer of the server, opening the editor right after an entry was added
function applyConfig(message) {
    const wanted = configPending;
    configPending = "";
    configEditing = "";
    configAdded = "";

    const list = document.getElementById("config-list");
    const empty = document.getElementById("config-empty");

    if (!message.ok) {
        configEntries = [];
        list.textContent = "";
        empty.textContent = message.error || gui.texts["config.empty"];
        empty.classList.remove("hidden");
        return;
    }

    empty.classList.add("hidden");
    configEntries = message.entries || [];

    if (wanted) {
        configEditing = wanted;
        configAdded = wanted;
    }

    drawConfig();
}

//renderConfigTexts redraws the rows after a language change, since the value labels are translated here
function renderConfigTexts() {
    if (configEntries.length > 0) {
        drawConfig();
    }
}

function drawConfig() {
    const list = document.getElementById("config-list");
    configGeneration++;
    list.textContent = "";

    let group = "";
    for (const entry of configEntries) {
        if (entry.group !== group) {
            group = entry.group;
            const title = document.createElement("div");
            title.className = "config-group";
            title.textContent = group;
            list.append(title);
        }

        list.append(configRow(entry));
    }
}

function configRow(entry) {
    const row = document.createElement("div");
    row.className = "config-row " + (entry.present ? "present" : "absent");
    if (entry.required) {
        row.classList.add("required");
        row.title = gui.texts["config.required"];
    }

    const name = document.createElement("span");
    name.className = "config-name";
    name.textContent = entry.label;
    name.title = entry.key;

    const value = document.createElement("span");
    value.className = "config-value";
    value.textContent = entry.present
        ? entry.value === ""
            ? gui.texts["settings.empty"]
            : entry.value
        : gui.texts["config.default"] + " " + (entry.default === "" ? gui.texts["settings.empty"] : entry.default);
    value.title = value.textContent;

    row.append(name, value);

    if (configEditing === entry.key) {
        row.append(configEditor(entry));
    }

    row.addEventListener("click", () => {
        if (configEditing === entry.key) {
            return;
        }

        //an absent entry is added with its default first, the editor then replaces that value
        if (!entry.present) {
            configPending = entry.key;
            vscode.postMessage({ type: "configSet", key: entry.key, value: entry.default });
            return;
        }

        configEditing = entry.key;
        configAdded = "";
        drawConfig();
    });

    if (entry.present && !entry.required) {
        const remove = document.createElement("button");
        remove.className = "config-remove";
        remove.textContent = "✕";
        remove.title = gui.texts["config.remove"];
        remove.addEventListener("click", (event) => {
            event.stopPropagation();
            configEditing = "";
            configAdded = "";
            vscode.postMessage({ type: "configSet", key: entry.key, remove: true });
        });
        row.append(remove);
    }

    return row;
}

function configEditor(entry) {
    const wrapper = document.createElement("span");
    wrapper.className = "config-editor";

    const generation = configGeneration;
    let input;

    if (entry.kind === "bool" || entry.kind === "enum") {
        input = document.createElement("select");
        const options = entry.kind === "bool" ? ["true", "false"] : entry.options;
        for (const option of options) {
            const node = document.createElement("option");
            node.value = option;
            node.textContent = option;
            node.selected = option === entry.value;
            input.append(node);
        }
    } else {
        input = document.createElement("input");
        input.value = entry.value;
        input.spellcheck = false;
        input.autocomplete = "off";
    }

    const commit = () => {
        //a redraw replaced this editor, so the value it holds belongs to a row that is gone
        if (generation !== configGeneration || configEditing !== entry.key) {
            return;
        }

        const added = configAdded === entry.key;
        const written = input.value;
        configEditing = "";
        configAdded = "";

        //an entry added without a value is dropped again, which is what clicking a gray row promises
        if (entry.kind === "text" && written.trim() === "") {
            vscode.postMessage({ type: "configSet", key: entry.key, remove: added });
            return;
        }

        vscode.postMessage({ type: "configSet", key: entry.key, value: written });
    };

    const cancel = () => {
        if (generation !== configGeneration || configEditing !== entry.key) {
            return;
        }

        const added = configAdded === entry.key && entry.value === "";
        configEditing = "";
        configAdded = "";

        if (added) {
            vscode.postMessage({ type: "configSet", key: entry.key, remove: true });
            return;
        }

        drawConfig();
    };

    input.addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
            event.preventDefault();
            commit();
        } else if (event.key === "Escape") {
            event.preventDefault();
            cancel();
        }
    });
    input.addEventListener("blur", commit);

    wrapper.append(input);
    setTimeout(() => {
        input.focus();
        if (input.select) {
            input.select();
        }
    }, 0);

    return wrapper;
}
