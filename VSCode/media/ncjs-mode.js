//A small javascript mode for the task editor
//It covers the syntax a task script uses, which keeps the page free of a second editor dependency
(function () {
    "use strict";

    var keywords = new Set(
        (
            "if else for while do switch case default break continue return function class extends " +
            "super new this throw try catch finally var let const typeof instanceof in of delete void " +
            "yield async await static get set import export from as"
        ).split(" ")
    );

    var atoms = new Set(["true", "false", "null", "undefined", "NaN", "Infinity"]);

    CodeMirror.defineMode("ncjs", function () {
        return {
            startState: function () {
                return { comment: false, template: false };
            },
            token: function (stream, state) {
                if (state.comment) {
                    if (stream.match(/^.*?\*\//)) {
                        state.comment = false;
                    } else {
                        stream.skipToEnd();
                    }
                    return "comment";
                }

                if (state.template) {
                    if (stream.match(/^[^`]*`/)) {
                        state.template = false;
                    } else {
                        stream.skipToEnd();
                    }
                    return "string";
                }

                if (stream.eatSpace()) {
                    return null;
                }

                if (stream.match("//")) {
                    stream.skipToEnd();
                    return "comment";
                }

                if (stream.match("/*")) {
                    state.comment = true;
                    if (stream.match(/^.*?\*\//)) {
                        state.comment = false;
                    } else {
                        stream.skipToEnd();
                    }
                    return "comment";
                }

                if (stream.match("`")) {
                    state.template = true;
                    if (stream.match(/^[^`]*`/)) {
                        state.template = false;
                    } else {
                        stream.skipToEnd();
                    }
                    return "string";
                }

                if (stream.match(/^"(?:[^"\\]|\\.)*"?/)) {
                    if (stream.string.charAt(stream.pos - 1) !== '"') {
                        stream.skipToEnd();
                    }
                    return "string";
                }

                if (stream.match(/^'(?:[^'\\]|\\.)*'?/)) {
                    if (stream.string.charAt(stream.pos - 1) !== "'") {
                        stream.skipToEnd();
                    }
                    return "string";
                }

                if (stream.match(/^(?:0[xX][\da-fA-F_]+|0[bB][01_]+|\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?)/)) {
                    return "number";
                }

                var start = stream.pos;
                if (stream.match(/^[A-Za-z_$][\w$]*/)) {
                    var word = stream.current();
                    if (keywords.has(word)) {
                        return "keyword";
                    }
                    if (atoms.has(word)) {
                        return "atom";
                    }
                    if (stream.string.charAt(start - 1) === ".") {
                        return "property";
                    }
                    if (stream.peek() === "(") {
                        return "def";
                    }
                    return "variable";
                }

                if (stream.match(/^[+\-*\/%=<>!&|^~?:]+/)) {
                    return "operator";
                }

                if (stream.match(/^[{}()[\]]/)) {
                    return "bracket";
                }

                stream.next();
                return "punctuation";
            },
        };
    });
})();
