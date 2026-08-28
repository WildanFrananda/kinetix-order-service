namespace Kinetix.OrderService.Application.Services;

public static class EnvLoader {
    public static void Load(string filePath = ".env") {
        if (!File.Exists(filePath)) return;

        foreach (var line in File.ReadAllLines(filePath)) {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var val = parts[1].Trim();

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) {
                Environment.SetEnvironmentVariable(key, val);
            }
        }
    }
}
