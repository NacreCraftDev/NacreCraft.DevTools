"use strict";

//The assembly page
//It keeps the loaded dll, the picked type and the per-tab results, and asks the extension one question at a time

let asmFile = "";
let asmSummary = null;
let asmSelected = "";
let asmTab = "code";
let asmResources = [];
let asmCache = {};
let asmSeq = 0;
let asmToken = 0;
const asmPending = new Map();

//the assembly page asks one question at a time and matches the answer by id
function asmRequest(mode, query) {
    return new Promise((resolve) => {
        const id = ++asmSeq;
        asmPending.set(id, resolve);
        vscode.postMessage({ type: "asm", id, file: asmFile, mode, query });
    });
}

function setAsmStatus(text) {
    document.getElementById("asm-status").textContent = text || "";
}

async function loadAssembly() {
    const value = document.getElementById("asm-file").value.trim();
    if (value.length === 0) {
        return;
    }

    asmFile = value;
    asmSummary = null;
    asmSelected = "";
    asmResources = [];
    asmCache = {};
    document.getElementById("asm-types").textContent = "";
    document.getElementById("asm-info").textContent = "";
    document.getElementById("asm-res-list").textContent = "";
    document.getElementById("asm-text").textContent = "";
    setAsmStatus(gui.texts["asm.loading"]);

    const result = await asmRequest("summary");
    if (!result.ok) {
        setAsmStatus(result.error || gui.texts["asm.failed"]);
        return;
    }

    asmSummary = result;
    document.getElementById("asm-info").textContent =
        result.name + "  " + result.version + "  ·  " +
        result.types.length + " " + gui.texts["asm.types"];
    renderTypes();
    setAsmStatus("");
    loadResources();
}

function renderTypes() {
    const list = document.getElementById("asm-types");
    list.textContent = "";
    if (!asmSummary) {
        return;
    }

    const filter = document.getElementById("asm-filter").value.trim().toLowerCase();
    for (const type of asmSummary.types) {
        if (filter.length > 0 && type.name.toLowerCase().indexOf(filter) < 0) {
            continue;
        }

        const row = document.createElement("div");
        row.className = "asm-type" + (type.name === asmSelected ? " active" : "");
        row.title = type.name;

        const kind = document.createElement("span");
        kind.className = "asm-kind";
        kind.textContent = type.kind;

        row.append(kind, document.createTextNode(type.name));
        row.addEventListener("click", () => selectType(type.name));
        list.append(row);
    }
}

function selectType(name) {
    asmSelected = name;
    renderTypes();
    showAsmTab(asmTab, true);
}

async function showAsmTab(tab, force) {
    asmTab = tab;
    document.querySelectorAll(".asm-tab").forEach((node) => {
        node.classList.toggle("active", node.dataset.tab === tab);
    });
    document.getElementById("asm-text").classList.toggle("hidden", tab === "res");
    document.getElementById("asm-res").classList.toggle("hidden", tab !== "res");

    if (tab === "res") {
        if (asmSummary) {
            loadResources();
        }
        return;
    }

    const target = asmSelected;
    if (target.length === 0 || !asmSummary) {
        document.getElementById("asm-text").textContent = gui.texts["asm.noSelection"];
        return;
    }

    const key = tab + "|" + target;
    if (!force && asmCache[key] !== undefined) {
        document.getElementById("asm-text").textContent = asmCache[key];
        return;
    }

    const mode = tab === "code" ? "decompile" : tab === "il" ? "il" : "type";
    document.getElementById("asm-text").textContent = gui.texts["asm.loading"];
    setAsmStatus(gui.texts["asm.loading"]);

    //a click on another type while this one is still compiling must not overwrite the newer result
    const token = ++asmToken;
    const result = await asmRequest(mode, target);
    if (token !== asmToken) {
        return;
    }

    if (!result.ok) {
        document.getElementById("asm-text").textContent = result.error || gui.texts["asm.failed"];
        setAsmStatus("");
        return;
    }

    asmCache[key] = result.text;
    document.getElementById("asm-text").textContent = result.text;
    setAsmStatus("");
}

async function loadResources() {
    if (asmFile.length === 0) {
        return;
    }

    const result = await asmRequest("resources");
    asmResources = result.ok ? result.resources || [] : [];
    renderResources(result.ok ? "" : result.error);
}

function renderResources(error) {
    const list = document.getElementById("asm-res-list");
    list.textContent = "";

    if (error) {
        setAsmStatus(error);
        return;
    }

    if (asmResources.length === 0) {
        const empty = document.createElement("div");
        empty.className = "placeholder";
        empty.textContent = gui.texts["asm.noResources"];
        list.append(empty);
        return;
    }

    for (const item of asmResources) {
        const row = document.createElement("div");
        row.className = "asm-res-row";

        const name = document.createElement("span");
        name.className = "asm-res-name";
        name.textContent = item.name;
        name.title = item.name;

        const meta = document.createElement("span");
        meta.className = "asm-res-kind";
        meta.textContent = (item.size >= 0 ? formatSize(item.size) + "  " : "") + item.kind;

        const actions = document.createElement("span");
        actions.className = "asm-res-actions";

        if (item.kind !== "linked") {
            const take = document.createElement("button");
            take.textContent = gui.texts["asm.extract"];
            take.addEventListener("click", () =>
                vscode.postMessage({ type: "asmExtract", file: asmFile, query: item.name })
            );
            actions.append(take);
        }

        const drop = document.createElement("button");
        drop.textContent = gui.texts["asm.remove"];
        drop.addEventListener("click", () =>
            vscode.postMessage({ type: "asmRemove", file: asmFile, query: item.name })
        );
        actions.append(drop);

        row.append(name, meta, actions);
        list.append(row);
    }
}

function formatSize(bytes) {
    return bytes < 1024 ? bytes + " B" : (bytes / 1024).toFixed(1) + " KB";
}

function renderAsmTexts() {
    document.getElementById("asm-filter").placeholder = gui.texts["asm.filter"] || "";
    document.getElementById("asm-file").placeholder = gui.texts["asm.path"] || "";
    if (asmSummary) {
        renderTypes();
        renderResources("");
    } else if (asmSelected.length === 0) {
        document.getElementById("asm-text").textContent = gui.texts["asm.empty"] || "";
    }
}
