// A stand-in sidecar (see the .csproj). Each start leaves a note in MANTLE_SIDECAR_STUB_DIR:
// start-<n>-<pid>.txt with its arguments. The first start exits by itself with code 3 after a moment
// (a "crash"), so the app's restart ladder starts a second one; that one, and every later one, sleeps
// forever: it never reads stdin, never sees its end, never watches --parent-pid.
string? dir = Environment.GetEnvironmentVariable("MANTLE_SIDECAR_STUB_DIR");
int n = 0;
if (!string.IsNullOrEmpty(dir)) {
    Directory.CreateDirectory(dir);
    n = Directory.GetFiles(dir, "start-*.txt").Length + 1;
    File.WriteAllText(Path.Combine(dir, $"start-{n}-{Environment.ProcessId}.txt"), string.Join(" ", args));
}
if (n == 1) { Thread.Sleep(1500); return 3; }
Thread.Sleep(Timeout.Infinite);
return 0;
