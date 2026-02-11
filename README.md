# afk-fishing-terraria

patch terraria to fish for you automatically cast once and walk away

### updated to version 1.4.5.5

works on windows linux and mac

## what it does

injects two patches into `Terraria.exe` using [dnlib](https://github.com/0xd4d/dnlib)

- **auto-catch** fish bites and it instantly reels in for you
- **auto-recast** holding a fishing rod with no bobber out makes it recast automatically, swap to a different item to stop it

## you need

- [net 10+](https://dotnet.microsoft.com/download)
- terraria on steam

## how to use

```
git clone https://github.com/fyiel/terraria-autofish.git
cd terraria-autofish/AutofishPatcher
dotnet run
```

if terraria isnt in the default steam path
```
TERRARIA_DIR="/your/path" dotnet run
```

then just open the game and fish

## undo

backup is made automatically as `Terraria.exe.bak` copy it back over or verify game files through steam

## how?

### 1 decompile the game

terraria is a net framework assembly so you can decompile it to readable c# with tools like [dnspy](https://github.com/dnSpy/dnSpy) or [ilspy](https://github.com/icsharpcode/ILSpy) or [dotpeek](https://www.jetbrains.com/decompiler/)

open `Terraria.exe` and browse the class tree, youre looking for the methods that handle whatever you want to change so in this case fishing

start by searching for obvious names like "fish" "bobber" "bait" etc since terraria's code isnt obfuscated so the names are readable

### 2 understand the game loop

read the decompiled methods and trace how they call each other

- `Projectile.AI_061_FishingBobber()` runs every frame for each bobber projectile and handles line physics and the bite timer and the nibble check
- `Player.ItemCheck()` runs every frame for the player and handles all item usage and only does anything when `controlUseItem` is true (left mouse button held)
- `Player.ItemCheck_CheckFishingBobber_ConsumeBait()` and `PullBobber()` are the internal methods that actually consume bait and spawn the caught item

`ai[1]` on the bobber goes negative when a fish bites, thats the trigger, and `controlUseItem` is the flag the game checks to know if youre clicking

### 3 pick a patching library

you need a library that can load and modify and save net assemblies at the IL level, the two main options are

- **[dnlib](https://github.com/0xd4d/dnlib)** works with terraria and handles the metadata correctly
- **Mono.Cecil** throws `BadImageFormatException` on terraria's assembly so dont bother

create a net console project and add dnlib from nuget (`dotnet add package dnlib`)

### 4 load the assembly and resolve references

use `ModuleDefMD.Load()` to load the game's bytes then find the types and fields and methods you need by name

```csharp
var projType = mod.Types.First(t => t.Name == "Projectile");
var aiField = projType.Fields.First(f => f.Name == "ai");
// etc
```

youll reference these when building IL instructions, every `ldfld` `call` `stfld` etc needs the actual field/method definition as its operand

### 5 find injection points by pattern not by index

IL instruction indices shift between game versions so dont hardcode them, instead scan for byte patterns, for example to find the nibble handler in the bobber AI you can do

```csharp
// look for ldarg.0 / ldfld ai / ldc.i4.1 / ldelem.r4 / ldc.r4 0 / bge.un
// preceded by a ret (end of previous block)
for (int i = 0; i < instrs.Count - 6; i++) {
    if (instrs[i].OpCode == OpCodes.Ldarg_0
        && instrs[i+1].OpCode == OpCodes.Ldfld && instrs[i+1].Operand == aiField
        // ...
```

this way your patcher survives minor updates that add or remove unrelated instructions

### 6 build and inject IL instructions

construct a `List<Instruction>` with the opcodes you want then insert them into the method's instruction list at the index you found, you need to think in stack-based IL

- `ldarg.0` pushes `this`
- `ldfld someField` pops the object and pushes the field value
- `call someMethod` pops args + instance and pushes return value
- `brfalse target` pops a value and branches if false

add any new local variables the injected code needs to the method's `Variables` list, use `Local` objects as operands for `ldloc`/`stloc`

### 7 fix up branch targets

if existing code has branches that jump to the instruction you inserted before then those branches now skip your injected code, scan all instructions before the injection point and redirect any that target the old instruction to point at your new first instruction instead

### 8 dont add fields or types

this is the biggest fuck you,  adding a new field to an existing type (or even to `<Module>`) changes the metadata table layout, terraria's static initializers (like `ItemID.Sets`) use hardcoded array indices that depend on metadata row numbers so shifting them causes `TypeInitializationException` on startup, only inject instructions into existing methods, dont touch the metadata structure

### 9 save with PreserveAll

when writing the modified assembly use `MetadataFlags.PreserveAll`

```csharp
var opts = new ModuleWriterOptions(mod) {
    MetadataOptions = { Flags = MetadataFlags.PreserveAll }
};
mod.Write(path, opts);
```

without this dnlib re-sorts metadata tables and breaks the same array-index assumptions mentioned above

### 10 test and iterate

back up the original exe and patch and launch the game and see if it crashes, if it does the crash log usually points to a corrupted static initializer or a bad IL sequence, for example

- **`TypeInitializationException`** you changed metadata layout, so remove any added fields/types
- **game runs but patch doesnt work** your injection point is wrong or the code runs at the wrong time in the game loop so double check when and where your code executes relative to input polling and projectile updates
- **`InvalidProgramException`** your IL is malformed, so your stack is unbalanced or wrong types or missing arguments etc, go check the IL sequence carefully

verify game files through steam to get a clean copy and try again