var end = DateTime.UtcNow.AddSeconds(300); double x = 0;
while (DateTime.UtcNow < end) { for (var i = 1; i < 1000; i++) x += Math.Sqrt(i) * Math.Sin(i); }
Console.WriteLine(x);
