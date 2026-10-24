//Entry bundled into media/textmate.js by esbuild
//The page drives the tokenizer, so both libraries are exposed on one global instead of a module loader

const oniguruma = require("vscode-oniguruma");
const textmate = require("vscode-textmate");

globalThis.NcTextMate = {
    loadWASM: oniguruma.loadWASM,
    OnigScanner: oniguruma.OnigScanner,
    OnigString: oniguruma.OnigString,
    Registry: textmate.Registry,
};
