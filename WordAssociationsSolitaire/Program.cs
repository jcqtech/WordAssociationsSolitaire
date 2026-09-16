if (args.Length > 0 && args[0] == "--selftest")
{
    return WordAssociationsSolitaire.SelfTest.Run();
}

if (args.Length > 0 && args[0] == "--demo")
{
    WordAssociationsSolitaire.Game1.StartupBoard = WordAssociationsSolitaire.DemoScene.Build();
}

using var game = new WordAssociationsSolitaire.Game1();
game.Run();
return 0;
