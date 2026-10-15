using AgentStudio.Setup;

if (args.FirstOrDefault() == "serve-native")
    return await NativeWebHost.RunAsync(args.Skip(1).ToArray());

return ProductSetup.IsProductCommand(args)
    ? await ProductSetup.RunAsync(args)
    : await SetupApplication.RunAsync(args);
