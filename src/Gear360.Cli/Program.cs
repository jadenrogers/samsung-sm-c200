using System.CommandLine;
using Gear360.Cli.Commands;

var root = new RootCommand("Copy photos and 360 videos off a Samsung Gear 360 (SM-C200) camera.");
root.Subcommands.Add(ListCommand.Create());
root.Subcommands.Add(CopyCommand.Create());
root.Subcommands.Add(StitchCommand.Create());
root.Subcommands.Add(FfmpegCommand.Create());

// Ctrl+C cancels the token passed to each command's action.
return await root.Parse(args).InvokeAsync();
