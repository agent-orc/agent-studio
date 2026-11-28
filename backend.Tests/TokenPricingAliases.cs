extern alias TaskServerAssembly;

// Task Server's Program remains aliased in this test host. Expose only the
// shared pricing contract and adapter to the existing pricing test fixtures.
global using ITokenPriceProvider = TaskServerAssembly::AgentStudio.Tokens.ITokenPriceProvider;
global using ITokenPricing = TaskServerAssembly::AgentStudio.Tokens.ITokenPricing;
global using TokenCostEstimate = TaskServerAssembly::AgentStudio.Tokens.TokenCostEstimate;
global using TokenEconomyPriceProvider = TaskServerAssembly::AgentStudio.Tokens.TokenEconomyPriceProvider;
