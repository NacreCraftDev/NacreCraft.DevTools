"use strict";

//NacreGui the rendering subsystem of the page
//Text never sits in the markup: every element names a translation key and this layer writes the active one, so a
//language change only swaps strings and never needs a new document. After writing it measures the result, which is
//what keeps a language with longer words from overflowing the space the layout was planned for
class NacreGui {
    constructor() {
        this.texts = {};
    }

    //setTexts writes the active translations and lays the page out again
    setTexts(texts) {
        this.texts = texts || {};
        this.apply();
        this.layout();
    }

    //apply writes every keyed element, keeping the full text reachable when a column has to clip it
    apply() {
        for (const node of document.querySelectorAll("[data-text]")) {
            const value = this.texts[node.dataset.text];
            if (value === undefined) {
                continue;
            }

            node.textContent = value;
            node.title = value;
        }
    }

    //layout measures what was written and widens the navigation to fit it
    //The bounds keep a very long language from eating the content area
    layout() {
        const nav = document.querySelector("nav");
        if (!nav) {
            return;
        }

        let widest = 0;
        for (const item of nav.querySelectorAll(".nav-item")) {
            widest = Math.max(widest, item.scrollWidth);
        }

        //the measurement already carries the item padding, the rest covers the region margin and its own padding
        const width = Math.min(Math.max(widest + 40, 140), 320);
        document.documentElement.style.setProperty("--nav-width", width + "px");
    }
}

window.nacreGui = new NacreGui();
