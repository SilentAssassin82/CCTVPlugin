# CCTV Camera Character Mod - Installation Guide

## 📷 **What This Mod Does:**

Replaces the fake client's astronaut model with a **tiny camera block**! Much less intrusive than a full character.

---

## 📁 **Installation:**

### **Step 1: Install the Mod**
1. Copy the entire `CCTVMod` folder to:
   ```
   %AppData%\SpaceEngineers\Mods\
   ```

2. Verify structure:
   ```
   SpaceEngineers\Mods\CCTVMod\
   ├── metadata.mod
   ├── modinfo.sbm
   └── Data\
       └── Characters.sbc
   ```

3. **Enable in-game:**
   - Load your world
   - Edit Settings → Mods
   - Find "CCTV Camera Character"
   - ✅ Enable it
   - Save and reload

---

### **Step 2: Configure Plugin to Use Camera Character**

The plugin needs to respawn the fake client with the camera character model.

**Edit:** `CCTVPlugin.cs` in the `TrySwitchCharacterModel` method to use:
```csharp
string targetSubtype = "CameraCharacter";
```

Then the plugin will automatically spawn your fake client as a tiny camera! 📷

---

## ✅ **Result:**

Instead of seeing a full astronaut flying around:
- ✅ **Tiny camera block** (much smaller!)
- ✅ **Thematic** (it IS a camera system!)
- ✅ **Less visible** (can hide in smaller spaces)
- ✅ **Same functionality** (still captures and transmits)

---

## 🎯 **Benefits:**

| Before (Astronaut) | After (Camera) |
|-------------------|----------------|
| ~2m tall character | ~0.25m camera cube |
| Obvious flying person | Small camera block |
| Hard to hide | Fits in tight spaces |
| Breaks immersion | Thematic and subtle |

---

## 🚀 **Usage:**

1. **Install mod** (see above)
2. **Start Torch server**
3. **Join with fake client account**
4. **Fake client spawns as camera!** 📷
5. **Run CCTVCapture.exe**
6. **Enjoy subtle CCTV!** ✨

---

## 📝 **Manual Respawn (Optional):**

If the fake client is already in-world as an astronaut:

1. **Kill the character** (respawn)
2. **In spawn menu**, select:
   - Character: **CCTV Camera Character**
3. **Respawn!**

The character will now be a tiny camera block! 🎉

---

## 🔧 **Troubleshooting:**

### "Mod not appearing in list"
- Check `%AppData%\SpaceEngineers\Mods\CCTVMod\` exists
- Verify `metadata.mod` and `Data\Characters.sbc` are present
- Restart Space Engineers

### "Character still looks like astronaut"
- Make sure mod is **enabled** in world settings
- **Respawn** the fake client character
- Check SE logs for mod loading errors

### "Character looks weird/broken"
- The camera model doesn't have humanoid bones
- This is normal! It's literally a camera cube
- Camera will appear as a small cube that flies around

---

## 🎨 **Future Ideas:**

Want it even smaller? Edit `Characters.sbc`:
```xml
<Model>Models\Cubes\Small\CameraBlock.mwm</Model>
```

Try other small models:
- `Models\Cubes\Small\ButtonPanel.mwm` (tiny button)
- `Models\Cubes\Small\Sensor.mwm` (sensor)
- `Models\Cubes\Small\Light.mwm` (light)
