# Desktop idle Star-writing loop

The desktop Star Writing Shelf now includes an opt-in background loop.

## Controls

- **Generate for every Star while idle** is unchecked by default.
- Checking it enables the loop and waits for eight seconds of foreground inactivity.
- **Start Writing Loop Now** checks/enables the loop and asks it to begin with the currently selected Star as soon as foreground conversation is clear.
- Unchecking the option cancels the active background writing request and stops the loop.

## Loop behavior

The loop cycles through Elusiaa, Kaelia, Maera, Khavia, Auerlis, and Nyxari. Each turn uses the existing two-pass writing pipeline to generate five pieces, rate them, apply persistence rules, and save the resulting writing state. It does not overwrite the main spoken-output or debug panels. The selected Star's shelf counters refresh when her background turn completes, and a mobile bridge shelf event is published.

After a successful Star turn, the loop waits one second before moving to the next Star. A failed Star turn waits five seconds and then continues with the next Star.

## Foreground preemption

Foreground activity always wins. The current background generation is cancelled and the idle clock is reset when any of these occur:

- a desktop conversation or requested Star reply starts or finishes;
- microphone speech begins;
- manual voice recording starts;
- the player cancels or changes runtime activity;
- an Android foreground text, voice, photo, manual Listen, writing-generation, or writing-performance request arrives.

The checkbox remains enabled during a pause. Once foreground activity has fully ended and eight quiet seconds have passed, the loop resumes at the same Star if that Star's interrupted turn did not complete.

Automatic private Android Listen prefetch checks are not treated as player foreground interaction. They continue to use their existing low-priority behavior.
