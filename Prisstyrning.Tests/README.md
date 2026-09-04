# Prisstyrning.Tests

This test project contains comprehensive tests for the ScheduleAlgorithm class to ensure we don't introduce bugs or regressions in the core schedule generation logic.

## What We Test

### Core Functionality
- **Null/Empty Input Handling**: Ensures the algorithm gracefully handles invalid or missing price data
- **Schedule Generation**: Verifies that valid price data produces valid schedules
- **Logic Types**: Tests both `PerDayOriginal` and `CrossDayCheapestLimited` logic modes

### Edge Cases
- **Invalid Price Data**: Tests handling of malformed JSON, invalid dates, and invalid price values
- **Configuration Parameters**: Validates that comfort hours, turn-off percentiles, and activation limits work correctly
- **Boundary Conditions**: Tests edge cases like very high/low percentiles and activation limits

### Safety Mechanisms
- **Activation Limits**: Ensures the algorithm respects maximum activations per day to prevent over-switching
- **Turn-off Limits**: Verifies that consecutive turn-off hours don't exceed configured maximums
- **Data Validation**: Confirms the algorithm handles corrupt or missing data without crashing

## Test Structure

All tests follow the Arrange-Act-Assert pattern:
1. **Arrange**: Set up test data and configuration
2. **Act**: Call the ScheduleAlgorithm.Generate method
3. **Assert**: Verify the output meets expectations

## Running Tests

```bash
# Run all tests
dotnet test

# Run with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run specific test
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

## Isolated PostgreSQL acceptance

The normal suite always compiles the PostgreSQL acceptance test and reports it as skipped unless an explicit disposable database is supplied. Run the guarded harness from the repository root:

```powershell
powershell -File scripts/test-postgres-acceptance.ps1
```

The harness starts PostgreSQL 17 in a temporary Docker container, publishes only a random loopback port, applies the real migrations and removes the container afterwards. The test refuses non-loopback hosts, database names without the `prisstyrning_acceptance_` prefix, PostgreSQL older than 17 and databases that already contain public tables. It must never be pointed at production or another persistent database.

## Helper Methods

- `CreatePriceData()`: Creates properly formatted JSON price data for testing
- Test configuration setup ensures consistent test environments

This test suite helps prevent "stupid" mistakes by catching regressions in the core scheduling logic that could lead to incorrect heating schedules or system failures.
