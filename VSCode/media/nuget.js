"use strict";

//The NuGet page
//Both tabs render the same list box, and the pane on its left describes whichever entry is picked

let nugetTab = "installed";
let nugetRefs = null;
let nugetRefsError = "";
let nugetProjectKey = "";
let nugetResults = [];
let nugetSearched = false;
let nugetKernelOpen = false;
let nugetLoading = false;
let nugetSelected = "";
let nugetSources = [];
let nugetSeq = 0;
let nugetSearchToken = 0;
const nugetPending = new Map();
const nugetDescriptions = {};
const nugetDescribing = new Set();
const nugetReadmes = {};
const nugetReading = new Set();
const nugetVersions = {};
const nugetVersionLoading = new Set();
const nugetTargets = {};
const nugetIcon = window.__nacrecraftIcon;

//the package page asks one question at a time and matches the answer by id
function nugetRequest(type, payload) {
    return new Promise((resolve) => {
        const id = ++nugetSeq;
        nugetPending.set(id, resolve);
        vscode.postMessage(Object.assign({ type, id }, payload));
    });
}

function ensureNuget() {
    if (nugetRefs || nugetLoading) {
        return;
    }

    if (!project) {
        nugetRefsError = gui.texts["nuget.noProject"] || "";
        renderNuget();
        return;
    }

    loadReferences();
}

async function loadReferences() {
    nugetLoading = true;
    const result = await nugetRequest("nugetRefs", {});
    nugetLoading = false;
    applyReferences(result);
}

function applyReferences(result) {
    if (result.ok) {
        nugetRefs = Object.assign({}, result, {
            kernel: result.kernel || [],
            packages: result.packages || [],
        });
        nugetRefsError = "";
    } else {
        nugetRefs = null;
        nugetRefsError = result.error || gui.texts["nuget.failed"];
    }
    renderNuget();
}

async function changeReference(payload) {
    setNugetStatus(gui.texts["nuget.working"]);
    const result = await nugetRequest("nugetSet", payload);
    if (!result.ok) {
        setNugetStatus(result.error || gui.texts["nuget.failed"]);
        return;
    }

    setNugetStatus("");
    applyReferences(result);
}

//force asks the extension to drop the cached answer and fetch the feed again
async function searchPackages(force) {
    const query = document.getElementById("nuget-query").value.trim();
    const source = currentSource();
    //an older answer must not replace the hits of a newer search
    const token = ++nugetSearchToken;

    setNugetStatus(gui.texts["nuget.searching"]);
    const result = await nugetRequest("nugetSearch", {
        query,
        source,
        refresh: force === true,
    });
    if (token !== nugetSearchToken) {
        return;
    }

    nugetSearched = true;
    nugetResults = result.ok ? result.packages || [] : [];
    setNugetStatus(result.ok ? "" : result.error || gui.texts["nuget.failed"]);
    renderNuget();
}

function currentSource() {
    const select = document.getElementById("nuget-source");
    if (select.value) {
        return select.value;
    }

    return nugetSources.length > 0
        ? nugetSources[0]
        : "https://api.nuget.org/v3/index.json";
}

//the page shows the feed name rather than its api address
function sourceLabel(source) {
    try {
        return new URL(source).host.replace(/^api\./, "").replace(/^www\./, "");
    } catch {
        return source;
    }
}

function setNugetStatus(text) {
    document.getElementById("nuget-status").textContent = text || "";
}

function selectNugetTab(tab) {
    nugetTab = tab === "browse" ? "browse" : "installed";
    document.querySelectorAll(".nuget-tab").forEach((node) => {
        node.classList.toggle("active", node.dataset.ntab === nugetTab);
    });
    document.getElementById("nuget-bar").classList.toggle("hidden", nugetTab !== "browse");
    ensureNuget();
    if (nugetTab === "browse" && !nugetSearched) {
        searchPackages(false);
    }
    renderNuget();
}

function renderNugetTexts() {
    document.getElementById("nuget-query").placeholder =
        gui.texts["nuget.searchPlaceholder"] || "";

    const refresh = document.getElementById("nuget-refresh");
    refresh.textContent = "\u21BB";
    refresh.title = gui.texts["nuget.refresh"] || "";
}

function renderNuget() {
    const box = document.getElementById("nuget-box");
    box.textContent = "";

    if (!nugetRefs) {
        box.append(nugetNote(nugetRefsError || gui.texts["nuget.loading"]));
    } else if (nugetTab === "installed") {
        renderInstalledNuget(box, nugetRefs);
    } else {
        renderBrowseNuget(box, nugetRefs);
    }

    renderNugetDetail();
}

function renderInstalledNuget(box, refs) {
    box.append(nugetSection(gui.texts["nuget.defaults"]));
    box.append(nugetKernelEntry(refs, false));
    if (refs.modApi) {
        box.append(nugetModApiEntry(false));
    }

    box.append(nugetSection(gui.texts["nuget.thirdParty"]));
    if (refs.packages.length === 0) {
        box.append(nugetNote(gui.texts["nuget.noPackages"]));
        return;
    }

    for (const entry of refs.packages) {
        box.append(nugetPackageEntry(entry, false));
    }
}

function renderBrowseNuget(box, refs) {
    box.append(nugetSection(gui.texts["nuget.defaults"]));
    box.append(nugetKernelEntry(refs, true));
    if (!refs.modApi) {
        box.append(nugetModApiEntry(true));
    }

    box.append(nugetSection(gui.texts["nuget.thirdParty"]));
    if (nugetResults.length === 0) {
        box.append(
            nugetNote(
                gui.texts[nugetSearched ? "nuget.noResults" : "nuget.searchHint"]
            )
        );
        return;
    }

    for (const entry of nugetResults) {
        box.append(nugetPackageEntry(entry, true));
    }
}

//the pane on the right describes the picked entry, and asks the feed for a description a package alone lacks
function renderNugetDetail() {
    const pane = document.getElementById("nuget-detail");
    const splitter = document.getElementById("nuget-split");
    pane.textContent = "";

    const entry = selectedNugetEntry();
    if (!entry) {
        //nothing picked keeps the list full width, which is how the dialog starts
        pane.classList.add("collapsed");
        splitter.classList.add("collapsed");
        return;
    }

    pane.classList.remove("collapsed");
    splitter.classList.remove("collapsed");

    const head = document.createElement("div");
    head.className = "nuget-detail-head";
    head.append(nugetIconNode(entry.icon, "nuget-detail-icon"));

    const title = document.createElement("div");
    title.className = "nuget-detail-title";

    const name = document.createElement("span");
    name.className = "nuget-detail-name";
    name.textContent = entry.name;
    name.title = entry.name;
    title.append(name);

    if (entry.version) {
        const version = document.createElement("span");
        version.className = "nuget-detail-version";
        version.textContent = entry.version;
        title.append(version);
    }

    head.append(title);
    pane.append(head);

    if (entry.tag) {
        const tags = document.createElement("div");
        tags.className = "nuget-detail-tags";

        const tag = document.createElement("span");
        tag.className = "nuget-tag" + (entry.prerelease ? " pre" : " stable");
        tag.textContent = entry.tag;
        tags.append(tag);
        pane.append(tags);
    }

    const summary = document.createElement("div");
    summary.className = "nuget-detail-summary";

    //while the document is being fetched the summary is all there is, so it stays visible
    let documentText = entry.package
        ? nugetReadmes[entry.readmeKey]
        : entry.document;
    const loading = documentText === undefined;

    //a package without a readme falls back to its description, which the summary already shows
    if (!loading && documentText.length === 0) {
        documentText = entry.document;
    }

    if (entry.summary && entry.summary !== documentText) {
        summary.textContent = entry.summary;
    } else {
        summary.textContent = "";
    }
    if (summary.textContent.length > 0) {
        pane.append(summary);
    }

    //packages carry a version switch between the description and the document
    if (entry.package) {
        pane.append(nugetVersionBlock(entry.name, entry.version));
    }

    const doc = document.createElement("div");
    doc.className = "nuget-detail-doc";
    if (loading) {
        doc.textContent = gui.texts["nuget.loadingDoc"] || "";
    } else {
        renderMarkdown(doc, documentText);
    }
    pane.append(doc);
}

//the document is markdown, which is what package authors write descriptions in
function renderMarkdown(container, text) {
    container.textContent = "";

    if (!text) {
        container.textContent = gui.texts["nuget.noDescription"] || "";
        return;
    }

    if (window.marked) {
        container.innerHTML = window.marked.parse(text);
        return;
    }

    container.textContent = text;
}

//the installed version and the version the install button will use
function nugetVersionBlock(id, fallback) {
    const block = document.createElement("div");
    block.className = "nuget-version-block";

    const installed = nugetRefs.packages.find(
        (item) => item.id.toLowerCase() === id.toLowerCase()
    );

    if (installed) {
        const line = document.createElement("div");
        line.className = "nuget-version-line";
        line.textContent =
            (gui.texts["nuget.installedVersion"] || "") + " " + installed.version;
        block.append(line);
    }

    const line = document.createElement("div");
    line.className = "nuget-version-line";

    const label = document.createElement("span");
    label.textContent = gui.texts[installed ? "nuget.changeTo" : "nuget.installVersion"] || "";
    line.append(label, nugetVersionPicker(id, fallback));
    block.append(line);

    return block;
}

//the picker offers every published version, newest first, each marked stable or prerelease
function nugetVersionPicker(id, fallback) {
    const versions = nugetVersions[id];
    const target = targetVersion(id, fallback);

    if (versions === undefined) {
        const pending = document.createElement("span");
        pending.className = "nuget-detail-version";
        pending.textContent = gui.texts["nuget.versionsLoading"] || "";
        return pending;
    }

    const picker = document.createElement("span");
    picker.className = "nuget-picker";

    const current = document.createElement("button");
    current.className = "nuget-picker-current";

    const value = document.createElement("span");
    value.className = "nuget-picker-version";
    value.textContent = target;
    current.append(value, nugetTag(target));

    const caret = document.createElement("span");
    caret.className = "nuget-picker-caret";
    caret.textContent = "\u25BE";
    current.append(caret);

    const menu = document.createElement("div");
    menu.className = "nuget-picker-menu hidden";

    for (const version of versions) {
        const item = document.createElement("div");
        item.className = "nuget-picker-item" + (version === target ? " active" : "");

        const text = document.createElement("span");
        text.className = "nuget-picker-version";
        text.textContent = version;
        item.append(text, nugetTag(version));

        item.addEventListener("click", (event) => {
            event.stopPropagation();
            nugetTargets[id] = version;
            renderNugetDetail();
        });
        menu.append(item);
    }

    current.addEventListener("click", (event) => {
        event.stopPropagation();
        closeVersionMenus();

        //the menu is fixed, so the scroll box of the pane cannot clip it
        const rect = current.getBoundingClientRect();
        menu.style.left = rect.left + "px";
        menu.style.top = rect.bottom + 2 + "px";
        menu.style.minWidth = Math.max(rect.width, 220) + "px";
        menu.classList.remove("hidden");
    });

    picker.append(current, menu);
    return picker;
}

//the version the install button applies: the one picked, else the newest stable one
function targetVersion(id, fallback) {
    if (nugetTargets[id] !== undefined) {
        return nugetTargets[id];
    }

    const versions = nugetVersions[id] || [];
    const stable = versions.find((version) => version.indexOf("-") < 0);
    return stable || versions[0] || fallback || "";
}

function nugetTag(version) {
    const prerelease = version.indexOf("-") >= 0;
    const tag = document.createElement("span");
    tag.className = "nuget-tag " + (prerelease ? "pre" : "stable");
    tag.textContent = gui.texts[prerelease ? "nuget.prerelease" : "nuget.stable"] || "";
    return tag;
}

function closeVersionMenus() {
    for (const menu of document.querySelectorAll(".nuget-picker-menu")) {
        menu.classList.add("hidden");
    }
}

async function loadVersions(id) {
    if (nugetVersions[id] !== undefined || nugetVersionLoading.has(id)) {
        return;
    }

    nugetVersionLoading.add(id);
    const result = await nugetRequest("nugetVersions", {
        packageId: id,
        source: currentSource(),
    });
    nugetVersionLoading.delete(id);
    nugetVersions[id] = result.ok ? result.versions || [] : [];

    if (nugetSelected === "pkg:" + id) {
        renderNugetDetail();
    }
}

function selectedNugetEntry() {
    if (!nugetRefs || nugetSelected.length === 0) {
        return null;
    }

    if (nugetSelected === "kernel") {
        const text = gui.texts["nuget.kernelDesc"] || "";
        const list = nugetRefs.kernel.map((file) => "- " + file).join("\n");
        return {
            name: gui.texts["nuget.kernel"],
            summary: text,
            document: list.length > 0 ? list : text,
        };
    }

    if (nugetSelected === "modApi") {
        const text = gui.texts["nuget.modApiDesc"] || "";
        return {
            name: gui.texts["nuget.modApi"],
            summary: text,
            document: text,
        };
    }

    if (nugetSelected.indexOf("pkg:") !== 0) {
        return null;
    }

    const id = nugetSelected.slice(4);
    const hit = nugetResults.find((item) => item.id.toLowerCase() === id.toLowerCase());
    const installed = nugetRefs.packages.find(
        (item) => item.id.toLowerCase() === id.toLowerCase()
    );
    const entry = hit || installed;
    if (!entry) {
        return null;
    }

    const version = entry.version || "";
    const prerelease = version.indexOf("-") >= 0;
    const text = (hit && hit.description) || nugetDescriptions[id] || "";
    return {
        name: entry.id,
        version,
        icon: hit ? hit.icon : "",
        summary: text,
        document: text,
        //a package carries its document inside the nupkg, which the server reads once
        package: true,
        readmeKey: id + "@" + version,
        tag: gui.texts[prerelease ? "nuget.prerelease" : "nuget.stable"],
        prerelease,
    };
}

//an installed package carries no description in the project file, so the feed is asked once
async function nugetDescribe(id) {
    if (nugetDescriptions[id] !== undefined || nugetDescribing.has(id)) {
        return;
    }

    nugetDescribing.add(id);
    const result = await nugetRequest("nugetSearch", {
        query: id,
        source: currentSource(),
        refresh: false,
    });
    nugetDescribing.delete(id);

    const hit = result.ok
        ? (result.packages || []).find(
              (item) => item.id.toLowerCase() === id.toLowerCase()
          )
        : null;

    nugetDescriptions[id] = hit ? hit.description : "";

    for (const row of document.querySelectorAll(".nuget-row")) {
        if (row.dataset.key !== "pkg:" + id) {
            continue;
        }

        const desc = row.querySelector(".nuget-desc");
        if (desc) {
            desc.textContent = nugetDescriptions[id] || "";
        }
    }

    renderNugetDetail();
}

function selectNugetRow(key) {
    nugetSelected = key;
    document.querySelectorAll(".nuget-row").forEach((node) => {
        node.classList.toggle("selected", node.dataset.key === key);
    });

    renderNugetDetail();
    if (key.indexOf("pkg:") === 0) {
        const entry = selectedNugetEntry();
        if (entry && entry.version) {
            loadReadme(entry.name, entry.version, entry.readmeKey);
        }
        if (entry) {
            loadVersions(entry.name);
        }
        nugetDescribe(key.slice(4));
    }
}

//the readme comes out of the package file, so it is asked for once per version
async function loadReadme(id, version, key) {
    if (nugetReadmes[key] !== undefined || nugetReading.has(key)) {
        return;
    }

    nugetReading.add(key);
    const result = await nugetRequest("nugetReadme", {
        packageId: id,
        version,
        source: currentSource(),
    });
    nugetReading.delete(key);
    nugetReadmes[key] = result.ok ? result.text || "" : "";

    if (nugetSelected === "pkg:" + id) {
        renderNugetDetail();
    }
}

function nugetSection(text) {
    const node = document.createElement("div");
    node.className = "nuget-section";
    node.textContent = text || "";
    return node;
}

function nugetNote(text) {
    const node = document.createElement("div");
    node.className = "nuget-note";
    node.textContent = text || "";
    return node;
}

function nugetIconNode(source, className) {
    const icon = document.createElement("img");
    icon.className = className;
    icon.alt = "";
    icon.src = source || nugetIcon;
    if (source) {
        icon.addEventListener("error", () => {
            icon.src = nugetIcon;
        });
    }
    return icon;
}

function nugetKernelEntry(refs, browse) {
    const wrapper = document.createElement("div");
    const row = nugetRow({
        key: "kernel",
        name: gui.texts["nuget.kernel"],
        description: gui.texts["nuget.kernelDesc"],
        expandable: refs.kernel.length > 0,
        install: browse,
        //the kernel assemblies come with the project, so the button is shown but never acts
        disabled: true,
    });
    wrapper.append(row);

    if (nugetKernelOpen && refs.kernel.length > 0) {
        const list = document.createElement("div");
        list.className = "nuget-children";
        for (const file of refs.kernel) {
            const child = document.createElement("div");
            child.className = "nuget-child";
            child.textContent = file;
            child.title = file;
            list.append(child);
        }
        wrapper.append(list);
    }

    return wrapper;
}

function nugetModApiEntry(browse) {
    return nugetRow({
        key: "modApi",
        name: gui.texts["nuget.modApi"],
        description: gui.texts["nuget.modApiDesc"],
        install: browse,
        //this one is a project switch rather than a package, so it writes the project file
        action: () => changeReference({ kind: "modApi", remove: !browse }),
    });
}

function nugetPackageEntry(entry, browse) {
    const version = entry.version || "";
    const prerelease = version.indexOf("-") >= 0;

    return nugetRow({
        key: "pkg:" + entry.id,
        name: entry.id,
        version,
        icon: entry.icon,
        description: entry.description || nugetDescriptions[entry.id] || "",
        tag: gui.texts[prerelease ? "nuget.prerelease" : "nuget.stable"],
        prerelease,
        install: browse,
        //the check applies the picked version, which may be a change rather than a fresh install
        action: () => changeReference(
            browse
                ? {
                      kind: "package",
                      remove: false,
                      packageId: entry.id,
                      version: targetVersion(entry.id, entry.version),
                  }
                : { kind: "package", remove: true, packageId: entry.id }
        ),
    });
}

function nugetRow(options) {
    const row = document.createElement("div");
    row.className = "nuget-row" + (options.key === nugetSelected ? " selected" : "");
    row.dataset.key = options.key || "";
    if (options.key) {
        row.addEventListener("click", () => selectNugetRow(options.key));
    }

    const main = document.createElement("div");
    main.className = "nuget-row-main";

    const caret = document.createElement("span");
    caret.className = "nuget-caret";
    if (options.expandable) {
        caret.textContent = "\u25B6";
        if (nugetKernelOpen) {
            caret.classList.add("open");
        }
        caret.addEventListener("click", (event) => {
            event.stopPropagation();
            nugetKernelOpen = !nugetKernelOpen;
            renderNuget();
        });
    }

    const name = document.createElement("span");
    name.className = "nuget-name";
    name.textContent = options.name;

    main.append(caret, nugetIconNode(options.icon, "nuget-icon"), name);

    if (options.version) {
        const version = document.createElement("span");
        version.className = "nuget-version";
        version.textContent = options.version;
        main.append(version);
    }

    const action = document.createElement("button");
    action.className = "nuget-action " + (options.install ? "add" : "remove");
    action.textContent = options.install ? "\u2713" : "\u2715";
    action.title = gui.texts[options.install ? "nuget.install" : "nuget.remove"];

    if (options.disabled) {
        action.disabled = true;
        action.classList.add("disabled");
    } else {
        action.addEventListener("click", (event) => {
            event.stopPropagation();
            options.action();
        });
    }

    main.append(action);

    const sub = document.createElement("div");
    sub.className = "nuget-row-sub";

    if (options.tag) {
        const tag = document.createElement("span");
        tag.className = "nuget-tag" + (options.prerelease ? " pre" : " stable");
        tag.textContent = options.tag;
        sub.append(tag);
    }

    const desc = document.createElement("span");
    desc.className = "nuget-desc";
    desc.textContent = options.description || "";
    desc.title = options.description || "";
    sub.append(desc);

    row.append(main, sub);
    return row;
}

//the pane size comes from the settings, and a drag overrides it until the panel is pushed again
const nugetAppliedSize = { width: 0, height: 0 };

function applyNugetSize(items) {
    const width = nugetSize(items, "nuget.detailWidth", 440, 260, 1200);
    const height = nugetSize(items, "nuget.detailHeight", 560, 200, 1600);

    const pane = document.getElementById("nuget-detail");
    document.documentElement.style.setProperty(
        "--nuget-detail-width",
        width + "px"
    );
    pane.style.height = height + "px";
    document.getElementById("nuget-split").style.height = height + "px";
    nugetAppliedSize.width = width;
    nugetAppliedSize.height = height;
}

function nugetSize(items, key, fallback, min, max) {
    const item = items.find((entry) => entry.key === key);
    const value = parseInt(String(item ? item.value : ""), 10);
    if (isNaN(value)) {
        return fallback;
    }

    return Math.min(Math.max(value, min), max);
}

//the source list comes from the configuration, the first one being the default feed
function applyNugetSources(items) {
    const entry = items.find((item) => item.key === "nuget.sources");
    const sources = String(entry ? entry.value : "")
        .split(/[;,\r\n]+/)
        .map((source) => source.trim())
        .filter((source) => source.length > 0);

    if (sources.length === 0) {
        sources.push("https://api.nuget.org/v3/index.json");
    }

    const select = document.getElementById("nuget-source");
    const current = select.value;
    select.textContent = "";

    for (const source of sources) {
        const option = document.createElement("option");
        option.value = source;
        option.textContent = sourceLabel(source);
        select.append(option);
    }

    nugetSources = sources;
    select.value = sources.indexOf(current) >= 0 ? current : sources[0];

    //one feed needs no picker, and the address itself is never shown
    select.classList.toggle("hidden", sources.length < 2);
}

//a size the user drags out, by the splitter or by the corner of the pane, is written back to the settings
let nugetSizeSave;
const nugetPane = document.getElementById("nuget-detail");
new ResizeObserver(() => {
    if (nugetPane.classList.contains("collapsed")) {
        return;
    }

    document.getElementById("nuget-split").style.height = nugetPane.offsetHeight + "px";

    //the size the settings asked for needs no write back
    if (
        nugetPane.offsetWidth === nugetAppliedSize.width &&
        nugetPane.offsetHeight === nugetAppliedSize.height
    ) {
        return;
    }

    clearTimeout(nugetSizeSave);
    nugetSizeSave = setTimeout(() => {
        if (nugetPane.classList.contains("collapsed")) {
            return;
        }

        const width = nugetPane.offsetWidth;
        const height = nugetPane.offsetHeight;
        if (width < 100 || height < 100) {
            return;
        }

        if (width !== nugetAppliedSize.width) {
            vscode.postMessage({
                type: "update",
                key: "nuget.detailWidth",
                value: String(width),
            });
        }
        if (height !== nugetAppliedSize.height) {
            vscode.postMessage({
                type: "update",
                key: "nuget.detailHeight",
                value: String(height),
            });
        }
    }, 400);
}).observe(nugetPane);

//a click outside the picker or a scroll of the pane closes the version menu
document.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest(".nuget-picker")) {
        return;
    }
    closeVersionMenus();
});
document.getElementById("nuget-detail").addEventListener("scroll", closeVersionMenus);
window.addEventListener("resize", closeVersionMenus);
