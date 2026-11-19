#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstdio>

// NOINLINE keeps every function visible as its own frame in the profile.
#define NOINLINE __declspec(noinline)

NOINLINE double Spin(int iterations)
{
    double s = 0;
    for (int i = 0; i < iterations; i++) s += std::sqrt(i) * std::sin(i);
    return s;
}

// Burns CPU for a given time (a Sleep would not show up in a CPU profile).
NOINLINE double BurnMs(int ms)
{
    double s = 0;
    auto start = std::chrono::steady_clock::now();
    while (std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - start).count() < ms)
        s += Spin(1000);
    return s;
}

NOINLINE double Level3() { return Spin(50000); }
NOINLINE double Level2() { return Level3() + Spin(20000); }
NOINLINE double Level1() { return Level2() + Spin(10000); }

// The mystery function: only runs ~1 in 4 loops and burns a random 50-400 ms.
NOINLINE double RandomHotspot() { return BurnMs(50 + rand() % 350); }

NOINLINE double Iteration()
{
    double t = Level1();
    if (rand() % 4 == 0) t += RandomHotspot();
    return t;
}

int main()
{
    double total = 0;
    for (;;) total += Iteration();
    return (int)total;
}
