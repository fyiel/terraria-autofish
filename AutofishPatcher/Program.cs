using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;

string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    var hash = SHA256.HashData(stream);
    return Convert.ToHexString(hash);
}

// set TERRARIA_DIR to change the target directory, otherwise it defaults to the common Steam install paths
string terrariaDir = Environment.GetEnvironmentVariable("TERRARIA_DIR")
    ?? (OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/Terraria/Terraria.app/Contents/Resources")
        : OperatingSystem.IsWindows()
            ? @"C:\Program Files (x86)\Steam\steamapps\common\Terraria"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local/share/Steam/steamapps/common/Terraria"));

string exeName = "Terraria.exe";
string terrariaExe = Path.Combine(terrariaDir, exeName);
string backupExe = terrariaExe + ".bak";
string hashFile = terrariaExe + ".patched.sha256";

if (File.Exists(backupExe))
{
    if (!File.Exists(hashFile))
    {
        // no hash file (upgrading from older patcher), stale bak, start fresh
        File.Delete(backupExe);
        File.Copy(terrariaExe, backupExe);
        Console.WriteLine("No hash file found, replaced stale backup.");
    }
    else
    {
        var currentHash = HashFile(terrariaExe);
        var patchedHash = File.ReadAllText(hashFile).Trim();

        if (currentHash == patchedHash)
        {
            // exe is still our patched version, restore original from backup
            File.Copy(backupExe, terrariaExe, true);
            Console.WriteLine("Restored original from backup.");
        }
        else
        {
            // exe changed since we patched it (game update), make a new backup
            File.Copy(terrariaExe, backupExe, true);
            Console.WriteLine("Detected game update, backup replaced with new version.");
        }
    }
}
else
{
    File.Copy(terrariaExe, backupExe);
    Console.WriteLine($"Backup created: {backupExe}");
}

Console.WriteLine("Loading Terraria.exe...");
var data = File.ReadAllBytes(terrariaExe);
var mod = ModuleDefMD.Load(data);

// resolve the types, fields, and methods we need
var allTypes = mod.Types.ToList();
allTypes.AddRange(allTypes.SelectMany(t => t.NestedTypes).ToList());

var projType = allTypes.First(t => t.Name == "Projectile");
var playerType = allTypes.First(t => t.Name == "Player");
var mainType = allTypes.First(t => t.Name == "Main");
var itemType = allTypes.First(t => t.Name == "Item");
var entityType = allTypes.First(t => t.Name == "Entity");

var aiField = projType.Fields.First(f => f.Name == "ai" && f.FieldType.TypeName == "Single[]");
var ownerField = projType.Fields.First(f => f.Name == "owner");
var activeField = projType.Fields.First(f => f.Name == "active");
var bobberFieldProj = projType.Fields.First(f => f.Name == "bobber");
var mainPlayerField = mainType.Fields.First(f => f.Name == "player" && f.IsStatic);
var myPlayerField = mainType.Fields.First(f => f.Name == "myPlayer" && f.IsStatic);
var mainProjectileField = mainType.Fields.First(f => f.Name == "projectile" && f.IsStatic);
var controlUseItemField = playerType.Fields.First(f => f.Name == "controlUseItem");
var releaseUseItemField = playerType.Fields.First(f => f.Name == "releaseUseItem");
var whoAmIField = entityType.Fields.First(f => f.Name == "whoAmI");
var fishingPoleField = itemType.Fields.First(f => f.Name == "fishingPole");

var consumeBait = playerType.Methods.First(m => m.Name == "ItemCheck_CheckFishingBobber_ConsumeBait");
var pullBobber = playerType.Methods.First(m => m.Name == "ItemCheck_CheckFishingBobber_PullBobber");
var killMethod = projType.Methods.First(m => m.Name == "Kill" && m.Parameters.Count == 1);

// 1st patch (auto-catch)
// In Projectile.AI_061_FishingBobber, find the "nibble" section where the game
// checks if a fish has bitten (ai[1] < 0). We inject code right at the start of
// that block to instantly catch the fish and kill the bobber.
Console.WriteLine("\n--- Patch 1: Auto-catch when fish bites ---");

var bobberAI = projType.Methods.First(m => m.Name == "AI_061_FishingBobber");
var instrs = bobberAI.Body.Instructions;

// find the nibble check: this.ai[1] >= 0f. this pattern is unique in the method,
// it's the only place ai[1] is compared to 0f with bge.un
int nibbleStart = -1;
for (int i = 0; i < instrs.Count - 6; i++)
{
    if (instrs[i].OpCode == OpCodes.Ldarg_0
        && instrs[i + 1].OpCode == OpCodes.Ldfld && instrs[i + 1].Operand == aiField
        && instrs[i + 2].OpCode == OpCodes.Ldc_I4_1
        && instrs[i + 3].OpCode == OpCodes.Ldelem_R4
        && instrs[i + 4].OpCode == OpCodes.Ldc_R4 && (float)instrs[i + 4].Operand == 0f
        && instrs[i + 5].OpCode == OpCodes.Bge_Un)
    {
        nibbleStart = i;
        break;
    }
}

if (nibbleStart < 0) { Console.WriteLine("ERROR: Could not find nibble handler in AI_061_FishingBobber"); return; }
Console.WriteLine($"  Found nibble handler at instruction {nibbleStart}");

int injectAt = nibbleStart + 6;

var baitLocal = new Local(mod.CorLibTypes.Int32, "autofishBait");
bobberAI.Body.Variables.Add(baitLocal);
var playerLocal = new Local(playerType.ToTypeSig(), "autofishPlayer");
bobberAI.Body.Variables.Add(playerLocal);

var originalCode = instrs[injectAt];
var p1 = new List<Instruction>();

// peusodecode for the injected logic:
//   var player = Main.player[this.owner];
//   if (Main.myPlayer != this.owner) goto original;  // only for local player
//   if (!player.ConsumeBait(this, out int baitType)) goto original;
//   player.PullBobber(this, baitType);  // spawn the caught item
//   this.Kill();                        // destroy the bobber
//   return;

p1.Add(OpCodes.Ldsfld.ToInstruction(mainPlayerField));
p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Ldfld, ownerField));
p1.Add(OpCodes.Ldelem_Ref.ToInstruction());
p1.Add(new Instruction(OpCodes.Stloc, playerLocal));

p1.Add(OpCodes.Ldsfld.ToInstruction(myPlayerField));
p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Ldfld, ownerField));
p1.Add(new Instruction(OpCodes.Bne_Un, originalCode));

p1.Add(OpCodes.Ldc_I4_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Stloc, baitLocal));

p1.Add(new Instruction(OpCodes.Ldloc, playerLocal));
p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Ldloca, baitLocal));
p1.Add(new Instruction(OpCodes.Call, consumeBait));
p1.Add(new Instruction(OpCodes.Brfalse, originalCode));

p1.Add(new Instruction(OpCodes.Ldloc, playerLocal));
p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Ldloc, baitLocal));
p1.Add(new Instruction(OpCodes.Call, pullBobber));

p1.Add(OpCodes.Ldarg_0.ToInstruction());
p1.Add(new Instruction(OpCodes.Call, killMethod));

p1.Add(OpCodes.Ret.ToInstruction());

for (int i = 0; i < p1.Count; i++)
    instrs.Insert(injectAt + i, p1[i]);

Console.WriteLine($"  Injected {p1.Count} instructions");

// 2nd patch (auto-recast)
// In Player.ItemCheck, the game only processes item use when controlUseItem is true
// (i.e., the player is clicking). We inject a check right before that gate:
// if the held item is a fishing rod and no bobbers are active, force a click.
Console.WriteLine("\n--- Patch 2: Auto-recast when no bobbers ---");

var itemCheck = playerType.Methods.First(m => m.Name == "ItemCheck" && m.Body.Instructions.Count > 2000);
var icInstrs = itemCheck.Body.Instructions;

// find the "controlUseItem" gate, the if-check that guards all item-use logic.
// primary: preceded by stfld releaseUseItem (works across versions).
// fallback: followed by ldfld releaseUseItem / brfalse (the double-gate pattern).
int gateIdx = -1;
for (int i = 1; i < icInstrs.Count - 2; i++)
{
    if (icInstrs[i].OpCode == OpCodes.Ldarg_0
        && icInstrs[i + 1].OpCode == OpCodes.Ldfld && icInstrs[i + 1].Operand == controlUseItemField
        && icInstrs[i + 2].OpCode == OpCodes.Brfalse
        && icInstrs[i - 1].OpCode == OpCodes.Stfld && icInstrs[i - 1].Operand == releaseUseItemField)
    {
        gateIdx = i;
        break;
    }
}
if (gateIdx < 0)
{
    // fallback: find controlUseItem / brfalse followed by releaseUseItem / brfalse
    for (int i = 0; i < icInstrs.Count - 6; i++)
    {
        if (icInstrs[i].OpCode == OpCodes.Ldarg_0
            && icInstrs[i + 1].OpCode == OpCodes.Ldfld && icInstrs[i + 1].Operand == controlUseItemField
            && icInstrs[i + 2].OpCode == OpCodes.Brfalse
            && icInstrs[i + 3].OpCode == OpCodes.Ldarg_0
            && icInstrs[i + 4].OpCode == OpCodes.Ldfld && icInstrs[i + 4].Operand == releaseUseItemField
            && icInstrs[i + 5].OpCode == OpCodes.Brfalse)
        {
            gateIdx = i;
            Console.WriteLine("  (used fallback pattern for gate detection)");
            break;
        }
    }
}

if (gateIdx < 0) { Console.WriteLine("ERROR: Could not find controlUseItem gate in ItemCheck"); return; }
Console.WriteLine($"  Found controlUseItem gate at instruction {gateIdx}");

var itemAnimationField = playerType.Fields.First(f => f.Name == "itemAnimation");

var loopVar = new Local(mod.CorLibTypes.Int32, "afLoopIdx");
itemCheck.Body.Variables.Add(loopVar);
var projVar = new Local(projType.ToTypeSig(), "afProj");
itemCheck.Body.Variables.Add(projVar);

var gateTarget = icInstrs[gateIdx];
var p2 = new List<Instruction>();

// pseudocode for the injected logic:
//   if (heldItem.fishingPole <= 0) goto normal;       // not a fishing rod
//   if (this.itemAnimation != 0) goto normal;         // mid-swing
//   for (int i = 0; i < 1000; i++) {
//       var p = Main.projectile[i];
//       if (p.active && p.owner == this.whoAmI && p.bobber)
//           goto normal;  // bobber still out, don't recast
//   }
//   this.controlUseItem = true;   // simulate a left click
//   this.releaseUseItem = true;

// skip if not a fishing rod
p2.Add(OpCodes.Ldloc_1.ToInstruction());
p2.Add(new Instruction(OpCodes.Ldfld, fishingPoleField));
p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Ble, gateTarget));

// skip when mid-animation (casting/swinging)
p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Ldfld, itemAnimationField));
p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Bne_Un, gateTarget));

// scan all 1000 projectile slots for an active bobber owned by the player
p2.Add(OpCodes.Ldc_I4_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, loopVar));

var loopStart = new Instruction(OpCodes.Ldsfld, mainProjectileField);
var loopEnd = new Instruction(OpCodes.Ldloc, loopVar);
p2.Add(new Instruction(OpCodes.Br, loopEnd));

p2.Add(loopStart);
p2.Add(new Instruction(OpCodes.Ldloc, loopVar));
p2.Add(OpCodes.Ldelem_Ref.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, projVar));

var loopIncrement = new Instruction(OpCodes.Ldloc, loopVar);
p2.Add(new Instruction(OpCodes.Ldloc, projVar));
p2.Add(new Instruction(OpCodes.Ldfld, activeField));
p2.Add(new Instruction(OpCodes.Brfalse, loopIncrement));

p2.Add(new Instruction(OpCodes.Ldloc, projVar));
p2.Add(new Instruction(OpCodes.Ldfld, ownerField));
p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(new Instruction(OpCodes.Ldfld, whoAmIField));
p2.Add(new Instruction(OpCodes.Bne_Un, loopIncrement));

p2.Add(new Instruction(OpCodes.Ldloc, projVar));
p2.Add(new Instruction(OpCodes.Ldfld, bobberFieldProj));
p2.Add(new Instruction(OpCodes.Brfalse, loopIncrement));

// don't recast when find a bobber
p2.Add(new Instruction(OpCodes.Br, gateTarget));

p2.Add(loopIncrement);
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(OpCodes.Add.ToInstruction());
p2.Add(new Instruction(OpCodes.Stloc, loopVar));

p2.Add(loopEnd);
p2.Add(new Instruction(OpCodes.Ldc_I4, 1000));
p2.Add(new Instruction(OpCodes.Blt, loopStart));

// force a click to recast when no bobbers are found
p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(new Instruction(OpCodes.Stfld, controlUseItemField));

p2.Add(OpCodes.Ldarg_0.ToInstruction());
p2.Add(OpCodes.Ldc_I4_1.ToInstruction());
p2.Add(new Instruction(OpCodes.Stfld, releaseUseItemField));

// redirect branches that originally targeted the gate to hit our check first
var firstNewInstr = p2[0];
for (int i = 0; i < gateIdx; i++)
{
    if (icInstrs[i].Operand == gateTarget)
    {
        icInstrs[i].Operand = firstNewInstr;
        Console.WriteLine($"  Fixed branch at [{i}] to point to autofish check");
    }
}

for (int i = 0; i < p2.Count; i++)
    icInstrs.Insert(gateIdx + i, p2[i]);

Console.WriteLine($"  Injected {p2.Count} instructions for auto-recast");

// preserveAll keeps the metadata tables intact so we don't break things like ItemID.Sets array indexing
Console.WriteLine("\nSaving patched Terraria.exe...");

var writerOptions = new dnlib.DotNet.Writer.ModuleWriterOptions(mod)
{
    MetadataOptions = {
        Flags = dnlib.DotNet.Writer.MetadataFlags.PreserveAll
    }
};

mod.Write(terrariaExe, writerOptions);

// save hash of patched exe so we can tell if Steam updates it later
File.WriteAllText(hashFile, HashFile(terrariaExe));

var origSize = new FileInfo(backupExe).Length;
var newSize = new FileInfo(terrariaExe).Length;
Console.WriteLine($"  Original: {origSize:N0} bytes");
Console.WriteLine($"  Patched:  {newSize:N0} bytes");

Console.WriteLine("\nDone! Autofish patch applied.");
Console.WriteLine("\nHow it works:");
Console.WriteLine("  1. Cast your fishing rod normally (one click)");
Console.WriteLine("  2. Fish bites -> auto-caught instantly (Patch 1)");
Console.WriteLine("  3. No bobbers detected -> auto-recast (Patch 2)");
Console.WriteLine("  4. Infinite loop! Switch to another item to stop.");
Console.WriteLine("\nTo restore: copy Terraria.exe.bak over Terraria.exe");
