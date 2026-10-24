"use strict";

//Shared handles of the page scripts
//They load in order and share one global scope, so these two are declared here rather than in each file
const vscode = acquireVsCodeApi();
const gui = window.nacreGui;
