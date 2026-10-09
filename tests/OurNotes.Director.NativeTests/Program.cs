// The real graphics tests run as a Native AOT executable. They need Mesa/EGL on
// Linux, but no game archive or redistributable Cubism SDK/model is involved.
try { return MaterialStateTests.Run(); }
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
