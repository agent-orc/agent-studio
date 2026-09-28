using AgentStudio.Setup;

return ProductSetup.IsProductCommand(args)
    ? await ProductSetup.RunAsync(args)
    : await SetupApplication.RunAsync(args);
