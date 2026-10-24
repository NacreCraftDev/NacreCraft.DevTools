using NacreCraft.ModBuild.Tools;
using NacreCraft.NcMod.LangServer;

//the task page compares a task name against the built-in tools, so they have to be known here as well
ToolRegistration.RegisterAll();

//stdout carries the protocol, so console output of the build tools is forwarded into the editor log instead
var protocol = Console.OpenStandardOutput();
var server = new LspServer(Console.OpenStandardInput(), protocol);
Console.SetOut(new LogWriter(server));
server.Run();
