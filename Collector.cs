using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace XadoSpec
{
    static class Collector
    {
        static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        const string Cimv2 = @"root\cimv2";
        const string Storage = @"root\Microsoft\Windows\Storage";
        const string StdCimv2 = @"root\StandardCimv2";

        sealed class Job
        {
            public string Title, Glyph, Color;
            public Action<Section, Report> Fill;
        }

        static Job J(string title, string glyph, string color, Action<Section, Report> fill) =>
            new Job { Title = title, Glyph = glyph, Color = color, Fill = fill };

        public static Report Collect()
        {
            var sw = Stopwatch.StartNew();
            var r = new Report();
            var jobs = new[]
            {
                J("Система", "", "#7C9CFF", Os),
                J("Процессор", "", "#FF8A65", Cpu),
                J("Материнская плата и BIOS", "", "#4DD0B1", Board),
                J("Оперативная память", "", "#B388FF", Ram),
                J("Видеокарта", "", "#66D17A", Gpu),
                J("Мониторы", "", "#4FC3F7", Displays),
                J("Накопители", "", "#FFD54F", Disks),
                J("Сеть", "", "#81D4FA", Net),
                J("Звук", "", "#F48FB1", Audio),
            };
            var tasks = jobs.Select(j => Task.Run(() =>
            {
                var s = new Section(j.Title, j.Glyph, j.Color);
                try { j.Fill(s, r); }
                catch (Exception e) { s.Note("Ошибка", e.Message); }
                return s;
            })).ToArray();
            Task.WaitAll(tasks);
            foreach (var t in tasks) r.Sections.Add(t.Result);
            r.Took = sw.Elapsed;
            return r;
        }

        static void Os(Section s, Report r)
        {
            var os = First("SELECT Caption, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");
            var nt = Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = RegS(nt, "CurrentBuildNumber");
            int.TryParse(build, out int buildNo);
            string ubr = RegO(nt, "UBR")?.ToString();

            string caption = S(os, "Caption");
            if (caption == null)
            {
                caption = RegS(nt, "ProductName");
                if (caption != null && buildNo >= 22000) caption = caption.Replace("Windows 10", "Windows 11");
            }
            caption = StripMs(caption);
            string ver = RegS(nt, "DisplayVersion") ?? RegS(nt, "ReleaseId");
            string fullBuild = ubr != null ? build + "." + ubr : build;

            s.Add("Windows", caption);
            s.Add("Версия", ver);
            s.Add("Сборка", fullBuild);
            s.Add("Разрядность", OsArch());
            s.Add("Установлена", CimDate(S(os, "InstallDate"), false));
            s.Add("Последний запуск", CimDate(S(os, "LastBootUpTime"), true));
            s.Add("Имя компьютера", Environment.MachineName);
            s.Add("Пользователь", Environment.UserName);

            int fw = 0;
            try { Native.GetFirmwareType(out fw); } catch { }
            s.Add("Режим загрузки", fw == 2 ? "UEFI" : fw == 1 ? "Legacy BIOS" : null);
            if (fw == 2)
            {
                var sb = RegO(Lm(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"), "UEFISecureBootEnabled");
                s.Add("Безопасная загрузка", sb == null ? null : Convert.ToInt32(sb) == 1 ? "включена" : "выключена");
            }

            try
            {
                uint rc = Native.Tbsi_GetDeviceInfo((uint)Marshal.SizeOf(typeof(Native.TPM_DEVICE_INFO)), out var tpm);
                if (rc == 0) s.Add("TPM", tpm.tpmVersion == 2 ? "2.0" : tpm.tpmVersion == 1 ? "1.2" : "есть");
                else if (rc == 0x8028400F) s.Add("TPM", "не обнаружен");
                else s.Add("TPM", null);
            }
            catch { s.Add("TPM", null); }

            if (Native.GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryFlag != 255)
            {
                string st = ps.ACLineStatus == 1 ? ((ps.BatteryFlag & 8) != 0 ? "заряжается" : "от сети") : "работает от батареи";
                s.Add("Батарея", ps.BatteryLifePercent <= 100 ? ps.BatteryLifePercent + "% · " + st : st);
            }

            r.Subtitle = string.Join(" · ", new[] { (caption + " " + ver).Trim(), "сборка " + fullBuild, Environment.MachineName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        static ushort NativeMachine()
        {
            try
            {
                if (Native.IsWow64Process2(Native.GetCurrentProcess(), out _, out ushort nm)) return nm;
            }
            catch (EntryPointNotFoundException) { }
            Native.GetNativeSystemInfo(out var si);
            switch (si.wProcessorArchitecture)
            {
                case 9: return 0x8664;
                case 12: return 0xAA64;
                case 0: return 0x014C;
                default: return 0;
            }
        }

        static string OsArch()
        {
            switch (NativeMachine())
            {
                case 0x8664: return "64-разрядная (x64)";
                case 0xAA64: return "64-разрядная (ARM64)";
                case 0x014C: return "32-разрядная (x86)";
                default: return null;
            }
        }

        sealed class Topo
        {
            public int Packages, Cores, Threads;
            public readonly SortedDictionary<int, int> CoresByClass = new SortedDictionary<int, int>();
            public readonly Dictionary<int, List<long>> Caches = new Dictionary<int, List<long>>();
        }

        static Topo Topology()
        {
            uint len = 0;
            Native.GetLogicalProcessorInformationEx(0xFFFF, IntPtr.Zero, ref len);
            if (len == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!Native.GetLogicalProcessorInformationEx(0xFFFF, buf, ref len)) return null;
                var t = new Topo();
                int gaSize = IntPtr.Size == 8 ? 16 : 12;
                for (int off = 0; off < len;)
                {
                    IntPtr p = IntPtr.Add(buf, off);
                    int rel = Marshal.ReadInt32(p);
                    int size = Marshal.ReadInt32(p, 4);
                    if (size <= 0) break;
                    if (rel == 0)
                    {
                        t.Cores++;
                        int eff = Marshal.ReadByte(p, 9);
                        int groups = Marshal.ReadInt16(p, 30);
                        for (int g = 0; g < groups; g++)
                        {
                            ulong mask = IntPtr.Size == 8
                                ? (ulong)Marshal.ReadInt64(p, 32 + g * gaSize)
                                : (uint)Marshal.ReadInt32(p, 32 + g * gaSize);
                            t.Threads += PopCount(mask);
                        }
                        t.CoresByClass.TryGetValue(eff, out int c);
                        t.CoresByClass[eff] = c + 1;
                    }
                    else if (rel == 3) t.Packages++;
                    else if (rel == 2)
                    {
                        int level = Marshal.ReadByte(p, 8);
                        long cs = (uint)Marshal.ReadInt32(p, 12);
                        if (!t.Caches.TryGetValue(level, out var list)) t.Caches[level] = list = new List<long>();
                        list.Add(cs);
                    }
                    off += size;
                }
                return t;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static int PopCount(ulong v)
        {
            int n = 0;
            for (; v != 0; v &= v - 1) n++;
            return n;
        }

        static void Cpu(Section s, Report r)
        {
            var procs = Q("SELECT Name, Manufacturer, MaxClockSpeed, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor");
            var p = procs.FirstOrDefault();
            string name = Squeeze(S(p, "Name")) ?? Squeeze(RegS(Lm(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"), "ProcessorNameString"));
            var t = Topology();

            s.Add("Модель", name);
            s.Add("Производитель", CpuVendor(S(p, "Manufacturer")));
            switch (NativeMachine())
            {
                case 0x8664: s.Add("Архитектура", "x86-64"); break;
                case 0xAA64: s.Add("Архитектура", "ARM64"); break;
                case 0x014C: s.Add("Архитектура", "x86"); break;
            }

            string coresText = null, threadsText = null;
            if (t != null && t.Cores > 0)
            {
                if (t.Packages > 1) s.Add("Процессоров", t.Packages.ToString());
                coresText = t.Cores.ToString();
                if (t.CoresByClass.Count == 2)
                {
                    int e = t.CoresByClass.First().Value, perf = t.CoresByClass.Last().Value;
                    coresText += $" ({perf} производительных + {e} энергоэффективных)";
                }
                else if (t.CoresByClass.Count > 2)
                {
                    coresText += " (по классам: " + string.Join(" + ", t.CoresByClass.Reverse().Select(x => x.Value)) + ")";
                }
                threadsText = t.Threads.ToString();
            }
            else
            {
                coresText = procs.Sum(x => (long)U(x, "NumberOfCores")).ToString();
            }
            s.Add("Ядер", coresText);
            s.Add("Потоков", threadsText);

            ulong mhz = U(p, "MaxClockSpeed");
            s.Add("Базовая частота", mhz == 0 ? null : mhz >= 1000 ? (mhz / 1000.0).ToString("0.00", Ru) + " ГГц" : mhz + " МГц");

            if (t != null)
            {
                foreach (int lvl in new[] { 1, 2, 3, 4 })
                {
                    if (!t.Caches.TryGetValue(lvl, out var list) || list.Count == 0) continue;
                    long total = list.Sum();
                    string text = KbMb(total);
                    if (lvl > 1 && list.Count > 1)
                        text += " (" + string.Join(" + ", list.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Count() + " × " + KbMb(g.Key))) + ")";
                    s.Add("Кэш L" + lvl, text);
                }
            }

            s.AddReal("Сокет", S(p, "SocketDesignation"));

            var cs = First("SELECT HypervisorPresent FROM Win32_ComputerSystem");
            var hv = P(cs, "HypervisorPresent");
            var vfe = P(p, "VirtualizationFirmwareEnabled");
            if (hv is bool h && h) s.Add("Виртуализация", "включена (работает гипервизор)");
            else if (vfe is bool v) s.Add("Виртуализация", v ? "включена" : "выключена в BIOS/UEFI");

            int tc = t?.Cores ?? 0, tt = t?.Threads ?? 0;
            r.Cpu = new Tile
            {
                Caption = "Процессор",
                Color = s.Color,
                Value = ShortCpu(name),
                Sub = tc > 0 ? Pl(tc, "ядро", "ядра", "ядер") + " · " + Pl(tt, "поток", "потока", "потоков") : null
            };
        }

        static string ShortCpu(string n)
        {
            if (n == null) return null;
            foreach (var cut in new[] { " with Radeon", " w/ Radeon", " CPU @", " @ " })
            {
                int i = n.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
                if (i > 0) n = n.Substring(0, i);
            }
            return Squeeze(n.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace(" Processor", "").Replace(" CPU", ""));
        }

        static string CpuVendor(string v)
        {
            if (v == null) return null;
            if (v == "GenuineIntel") return "Intel";
            if (v == "AuthenticAMD") return "AMD";
            return v;
        }

        static void Board(Section s, Report r)
        {
            var bb = First("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
            var cs = First("SELECT Manufacturer, Model, SystemFamily FROM Win32_ComputerSystem");
            var bios = First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS");

            s.Head("Материнская плата");
            s.AddReal("Производитель", S(bb, "Manufacturer"));
            s.AddReal("Модель", S(bb, "Product"));
            s.AddReal("Ревизия", S(bb, "Version"));

            s.Head("Компьютер");
            s.AddReal("Производитель", S(cs, "Manufacturer"));
            s.AddReal("Модель", S(cs, "Model"));
            s.AddReal("Серия", S(cs, "SystemFamily"));

            s.Head("BIOS / UEFI");
            s.AddReal("Производитель", S(bios, "Manufacturer"));
            s.AddReal("Версия", S(bios, "SMBIOSBIOSVersion"));
            s.Add("Дата выпуска", DateOnly(S(bios, "ReleaseDate")));
            ulong maj = U(bios, "SMBIOSMajorVersion"), min = U(bios, "SMBIOSMinorVersion");
            if (maj > 0) s.Add("Версия SMBIOS", maj + "." + min);

            string pcMan = Real(S(cs, "Manufacturer")), pcModel = Real(S(cs, "Model"));
            string bMan = Real(S(bb, "Manufacturer")), bModel = Real(S(bb, "Product"));
            if (pcModel != null) r.Title = pcMan != null && pcModel.IndexOf(pcMan, StringComparison.OrdinalIgnoreCase) < 0 ? pcMan + " " + pcModel : pcModel;
            else if (bModel != null) r.Title = bMan != null ? bMan + " " + bModel : bModel;
        }

        static void Ram(Section s, Report r)
        {
            ulong instKb = 0;
            try { Native.GetPhysicallyInstalledSystemMemory(out instKb); } catch { }
            var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) };
            bool msOk = Native.GlobalMemoryStatusEx(ref ms);

            var mods = Q("SELECT BankLabel, DeviceLocator, Capacity, Manufacturer, PartNumber, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType, FormFactor FROM Win32_PhysicalMemory");
            ulong modSum = (ulong)mods.Sum(m => (decimal)U(m, "Capacity"));
            ulong installed = instKb > 0 ? instKb * 1024 : modSum;

            s.Add("Установлено", installed > 0 ? Bin(installed) : null);
            if (msOk)
            {
                s.Add("Доступно Windows", Bin(ms.ullTotalPhys));
                s.Add("Занято сейчас", Bin(ms.ullTotalPhys - ms.ullAvailPhys) + " (" + ms.dwMemoryLoad + "%)");
            }

            var types = mods.Select(MemType).Where(x => x != null).Distinct().ToList();
            string type = types.Count == 1 ? types[0] : types.Count > 1 ? string.Join(" / ", types) : null;
            s.Add("Тип", type);

            var cfg = mods.Select(m => U(m, "ConfiguredClockSpeed")).Where(x => x > 0).Distinct().ToList();
            var rated = mods.Select(m => U(m, "Speed")).Where(x => x > 0).Distinct().ToList();
            var cur = cfg.Count > 0 ? cfg : rated;
            string speed = cur.Count > 0 ? string.Join(" / ", cur.Select(x => x + " МТ/с")) : null;
            s.Add("Скорость", speed);

            ulong slots = 0;
            try
            {
                foreach (var a in Q("SELECT MemoryDevices, Use FROM Win32_PhysicalMemoryArray"))
                    if (U(a, "Use") == 3) slots += U(a, "MemoryDevices");
            }
            catch { }
            if (mods.Count > 0) s.Add("Слотов занято", slots >= (ulong)mods.Count ? mods.Count + " из " + slots : mods.Count.ToString());

            foreach (var m in mods)
            {
                string loc = Real(S(m, "DeviceLocator")), bank = Real(S(m, "BankLabel"));
                string head = loc ?? bank ?? "Модуль";
                if (loc != null && bank != null && !string.Equals(loc, bank, StringComparison.OrdinalIgnoreCase)) head += " · " + bank;
                s.Head(head);
                s.Add("Объём", Bin(U(m, "Capacity")));
                s.AddReal("Производитель", JedecName(S(m, "Manufacturer")));
                s.AddReal("Партномер", S(m, "PartNumber"));
                ulong sp = U(m, "Speed"), cc = U(m, "ConfiguredClockSpeed");
                if (cc > 0) s.Add("Работает на", cc + " МТ/с");
                if (sp > 0) s.Add("Максимум по SMBIOS", sp + " МТ/с");
                string ff = FormFactor(U(m, "FormFactor"));
                string mt = MemType(m);
                s.Add("Тип", mt != null && ff != null ? mt + " " + ff : mt ?? ff);
            }

            string sizes = mods.Count > 0
                ? string.Join(" + ", mods.GroupBy(m => U(m, "Capacity")).Select(g => (g.Count() > 1 ? g.Count() + " × " : "") + Bin(g.Key)))
                : null;
            r.Ram = new Tile
            {
                Caption = "Оперативная память",
                Color = s.Color,
                Value = installed > 0 ? Bin(installed) + (type != null ? " " + type : "") : null,
                Sub = string.Join(" · ", new[] { speed, sizes }.Where(x => x != null))
            };
        }

        static string MemType(ManagementBaseObject m)
        {
            switch (U(m, "SMBIOSMemoryType"))
            {
                case 18: return "DDR";
                case 19: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 27: return "LPDDR";
                case 28: return "LPDDR2";
                case 29: return "LPDDR3";
                case 30: return "LPDDR4";
                case 34: return "DDR5";
                case 35: return "LPDDR5";
                case 36: return "HBM3";
            }
            switch (U(m, "MemoryType"))
            {
                case 20: return "DDR";
                case 21: return "DDR2";
                case 24: return "DDR3";
            }
            return null;
        }

        static string FormFactor(ulong ff)
        {
            switch (ff)
            {
                case 8: return "DIMM";
                case 12: return "SO-DIMM";
                case 13: return "SRIMM";
                default: return null;
            }
        }

        static string JedecName(string raw)
        {
            if (raw == null) return null;
            string k = raw.Trim().ToUpperInvariant();
            if (k.Length < 4) return raw;
            switch (k.Substring(0, 4))
            {
                case "80CE": case "CE00": return "Samsung";
                case "80AD": case "AD00": return "SK hynix";
                case "802C": case "2C00": return "Micron";
                case "0198": return "Kingston";
                case "029E": return "Corsair";
                case "04CD": return "G.Skill";
                case "059B": case "859B": return "Crucial";
                case "04CB": return "ADATA";
                case "04EF": return "Team Group";
                case "830B": return "Nanya";
            }
            return raw;
        }

        static void Gpu(Section s, Report r)
        {
            var vcs = Q("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, PNPDeviceID, AdapterRAM, ConfigManagerErrorCode FROM Win32_VideoController");
            string bestName = null;
            ulong bestVram = 0;
            int shown = 0;
            foreach (var vc in vcs)
            {
                string pnp = S(vc, "PNPDeviceID") ?? "";
                if (pnp.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) continue;
                shown++;
                string name = S(vc, "Name") ?? "Видеоадаптер";
                string vendor = S(vc, "AdapterCompatibility");
                var cls = DriverKey(pnp);
                ulong vram = RegBytes(cls, "HardwareInformation.qwMemorySize");
                if (vram == 0) vram = RegBytes(cls, "HardwareInformation.MemorySize");

                s.Head(name);
                s.Add("Производитель", vendor);
                if (vram > 0) s.Add("Видеопамять", Bin(vram));
                else
                {
                    ulong ar = U(vc, "AdapterRAM");
                    if (ar >= 0xFFF00000UL) s.Add("Видеопамять", "не меньше 4 ГБ");
                    else s.Add("Видеопамять", ar > 0 ? Bin(ar) : null);
                }

                string dv = S(vc, "DriverVersion");
                string extra = null;
                if (dv != null && vendor != null && vendor.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0) extra = NvidiaVersion(dv);
                string radeon = RegS(cls, "RadeonSoftwareVersion");
                if (radeon != null) extra = "Adrenalin " + radeon;
                s.Add("Драйвер", dv == null ? null : extra != null ? dv + " (" + extra + ")" : dv);
                s.Add("Дата драйвера", DateOnly(S(vc, "DriverDate")));
                string ids = DeviceIds(pnp);
                if (ids != null) s.Add("ID устройства", ids);
                ulong err = U(vc, "ConfigManagerErrorCode");
                if (err != 0) s.Add("Состояние", "ошибка устройства, код " + err);

                if (bestName == null || vram > bestVram) { bestName = name; bestVram = vram; }
            }
            if (shown == 0) s.Note("Видеоадаптеры", "не найдены");
            r.Gpu = new Tile
            {
                Caption = "Видеокарта",
                Color = s.Color,
                Value = bestName,
                Sub = bestVram > 0 ? Bin(bestVram) + " видеопамяти" : null
            };
        }

        static RegistryKey DriverKey(string pnp)
        {
            if (string.IsNullOrEmpty(pnp)) return null;
            string drv = RegS(Lm(@"SYSTEM\CurrentControlSet\Enum\" + pnp), "Driver");
            return drv == null ? null : Lm(@"SYSTEM\CurrentControlSet\Control\Class\" + drv);
        }

        static ulong RegBytes(RegistryKey k, string name)
        {
            var v = RegO(k, name);
            switch (v)
            {
                case long l: return (ulong)l;
                case int i: return (uint)i;
                case byte[] b when b.Length >= 8: return BitConverter.ToUInt64(b, 0);
                case byte[] b when b.Length >= 4: return BitConverter.ToUInt32(b, 0);
                default: return 0;
            }
        }

        static string NvidiaVersion(string dv)
        {
            var parts = dv.Split('.');
            if (parts.Length != 4) return null;
            string digits = parts[2] + parts[3].PadLeft(4, '0');
            if (digits.Length < 5) return null;
            digits = digits.Substring(digits.Length - 5);
            return "NVIDIA " + digits.Substring(0, 3) + "." + digits.Substring(3);
        }

        static string DeviceIds(string pnp)
        {
            int v = pnp.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase), d = pnp.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
            if (v < 0 || d < 0 || v + 8 > pnp.Length || d + 8 > pnp.Length) return null;
            return pnp.Substring(v, 8) + " " + pnp.Substring(d, 8);
        }

        static void Displays(Section s, Report r)
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            int err = Native.GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm);
            if (err != 0) throw new Win32Exception(err);
            var paths = new Native.DC_PATH[np];
            var modes = new Native.DC_MODE[nm];
            err = Native.QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (err != 0) throw new Win32Exception(err);

            var mons = MonitorsByGdi();
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var tn = new Native.DC_TARGET_NAME();
                tn.header.type = 2;
                tn.header.size = (uint)Marshal.SizeOf(typeof(Native.DC_TARGET_NAME));
                tn.header.adapterId = p.target.adapterId;
                tn.header.id = p.target.id;
                bool tnOk = Native.GetTargetName(ref tn) == 0;

                var sn = new Native.DC_SOURCE_NAME();
                sn.header.type = 1;
                sn.header.size = (uint)Marshal.SizeOf(typeof(Native.DC_SOURCE_NAME));
                sn.header.adapterId = p.source.adapterId;
                sn.header.id = p.source.id;
                bool snOk = Native.GetSourceName(ref sn) == 0;

                uint tech = p.target.outputTechnology;
                bool builtIn = tech == 0x80000000 || tech == 6 || tech == 11 || tech == 13;
                string name = tnOk && !string.IsNullOrWhiteSpace(tn.friendlyName) ? tn.friendlyName.Trim() : builtIn ? "Встроенный дисплей" : "Монитор";

                (bool primary, uint dpi) mi = (false, 0);
                if (snOk) mons.TryGetValue(sn.gdiName ?? "", out mi);
                s.Head(name + (mi.primary ? " · основной" : ""));

                uint mi2 = p.source.modeInfoIdx;
                if (mi2 < nm && modes[mi2].infoType == 1) s.Add("Разрешение", modes[mi2].width + " × " + modes[mi2].height);
                else s.Add("Разрешение", null);

                var rr = p.target.refreshRate;
                s.Add("Частота обновления", rr.Den == 0 ? null : ((double)rr.Num / rr.Den).ToString("0.##", Ru) + " Гц");
                s.Add("Масштаб", mi.dpi > 0 ? Math.Round(mi.dpi * 100.0 / 96) + "%" : null);
                s.Add("Подключение", Tech(tech));
            }
            if (np == 0) s.Note("Мониторы", "активных нет");
        }

        static Dictionary<string, (bool, uint)> MonitorsByGdi()
        {
            var d = new Dictionary<string, (bool, uint)>(StringComparer.OrdinalIgnoreCase);
            Native.MonitorEnumProc cb = (h, hdc, rc, data) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
                if (Native.GetMonitorInfo(h, ref mi))
                {
                    uint dx = 0;
                    try { Native.GetDpiForMonitor(h, 0, out dx, out _); } catch { }
                    d[mi.szDevice] = ((mi.dwFlags & 1) != 0, dx);
                }
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return d;
        }

        static string Tech(uint t)
        {
            switch (t)
            {
                case 0: return "VGA";
                case 4: return "DVI";
                case 5: return "HDMI";
                case 6: return "встроенный (LVDS)";
                case 10: return "DisplayPort";
                case 11: return "встроенный (eDP)";
                case 15: return "Miracast";
                case 16: case 17: return "косвенный (виртуальный)";
                case 18: return "DisplayPort через USB-C";
                case 0x80000000: return "встроенный";
                default: return null;
            }
        }

        static void Disks(Section s, Report r)
        {
            var letters = new Dictionary<int, List<char>>();
            try
            {
                foreach (var p in Q("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", Storage))
                {
                    var dl = P(p, "DriveLetter");
                    char c = dl is char ch ? ch : dl is ushort us ? (char)us : '\0';
                    c = char.ToUpperInvariant(c);
                    if (c < 'A' || c > 'Z') continue;
                    int dn = (int)U(p, "DiskNumber");
                    if (!letters.TryGetValue(dn, out var l)) letters[dn] = l = new List<char>();
                    l.Add(c);
                }
            }
            catch { }

            List<ManagementBaseObject> pds = null;
            try { pds = Q("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, FirmwareVersion FROM MSFT_PhysicalDisk", Storage); }
            catch { }

            ulong total = 0;
            int count = 0;
            if (pds != null && pds.Count > 0)
            {
                foreach (var d in pds.OrderBy(x => int.TryParse(S(x, "DeviceId"), out int k) ? k : 999))
                {
                    ulong size = U(d, "Size");
                    total += size;
                    count++;
                    s.Head(S(d, "FriendlyName") ?? "Накопитель");
                    s.Add("Тип", DiskType((int)U(d, "BusType"), (int)U(d, "MediaType")));
                    s.Add("Объём", size > 0 ? Dec(size) + " (в Windows — " + Bin(size) + ")" : null);
                    s.Add("Состояние", Health(U(d, "HealthStatus")));
                    s.AddReal("Прошивка", S(d, "FirmwareVersion"));
                    if (int.TryParse(S(d, "DeviceId"), out int num) && letters.TryGetValue(num, out var ls))
                        foreach (char c in ls.OrderBy(x => x)) AddVolume(s, c);
                }
            }
            else
            {
                foreach (var d in Q("SELECT Index, Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive").OrderBy(x => U(x, "Index")))
                {
                    ulong size = U(d, "Size");
                    total += size;
                    count++;
                    s.Head(S(d, "Model") ?? "Накопитель");
                    s.Add("Интерфейс", S(d, "InterfaceType"));
                    s.Add("Объём", size > 0 ? Dec(size) + " (в Windows — " + Bin(size) + ")" : null);
                    if (letters.TryGetValue((int)U(d, "Index"), out var ls))
                        foreach (char c in ls.OrderBy(x => x)) AddVolume(s, c);
                }
            }
            if (count == 0) s.Note("Накопители", "не найдены");
            r.Disk = new Tile
            {
                Caption = "Накопители",
                Color = s.Color,
                Value = total > 0 ? Dec(total) + " всего" : null,
                Sub = count > 0 ? Pl(count, "накопитель", "накопителя", "накопителей") : null
            };
        }

        static void AddVolume(Section s, char c)
        {
            try
            {
                var di = new DriveInfo(c + ":\\");
                if (!di.IsReady) return;
                string label = string.IsNullOrWhiteSpace(di.VolumeLabel) ? "" : " · " + di.VolumeLabel;
                s.Add("Раздел " + c, "свободно " + Bin((ulong)di.AvailableFreeSpace) + " из " + Bin((ulong)di.TotalSize) + label);
            }
            catch { }
        }

        static string DiskType(int bus, int media)
        {
            string b;
            switch (bus)
            {
                case 1: b = "SCSI"; break;
                case 3: b = "ATA"; break;
                case 7: b = "USB"; break;
                case 8: b = "RAID"; break;
                case 10: b = "SAS"; break;
                case 11: b = "SATA"; break;
                case 12: b = "SD-карта"; break;
                case 13: b = "eMMC"; break;
                case 15: return "виртуальный диск (VHD)";
                case 16: return "дисковое пространство";
                case 17: b = "NVMe"; break;
                case 19: b = "UFS"; break;
                default: b = null; break;
            }
            string m = media == 4 ? "SSD" : media == 3 ? "HDD" : media == 5 ? "SCM" : bus == 17 ? "SSD" : null;
            if (m != null && b != null) return m + " · " + b;
            return m ?? b;
        }

        static string Health(ulong h)
        {
            switch (h)
            {
                case 0: return "исправен";
                case 1: return "предупреждение";
                case 2: return "неисправен";
                default: return null;
            }
        }

        static void Net(Section s, Report r)
        {
            var ads = Q("SELECT Name, InterfaceDescription, MediaConnectState, Speed, NdisPhysicalMedium, HardwareInterface, Virtual FROM MSFT_NetAdapter", StdCimv2);
            int shown = 0;
            foreach (var a in ads.OrderBy(x => S(x, "InterfaceDescription")))
            {
                if (!(P(a, "HardwareInterface") is bool hw) || !hw) continue;
                if (P(a, "Virtual") is bool v && v) continue;
                ulong medium = U(a, "NdisPhysicalMedium");
                if (medium == 10) continue;
                shown++;
                s.Head(S(a, "InterfaceDescription") ?? S(a, "Name") ?? "Адаптер");
                s.Add("Тип", medium == 9 ? "Wi-Fi" : medium == 14 ? "Ethernet" : medium == 8 ? "мобильная сеть" : "другой");
                s.Add("Имя в системе", S(a, "Name"));
                bool up = U(a, "MediaConnectState") == 1;
                ulong bps = U(a, "Speed");
                s.Add("Состояние", up ? "подключено" + (bps > 0 && bps < ulong.MaxValue ? " · " + Bps(bps) : "") : "не подключено");
            }
            if (shown == 0) s.Note("Адаптеры", "не найдены");
        }

        static string Bps(ulong bps)
        {
            if (bps >= 1000000000) return (bps / 1e9).ToString("0.#", Ru) + " Гбит/с";
            return (bps / 1e6).ToString("0.#", Ru) + " Мбит/с";
        }

        static void Audio(Section s, Report r)
        {
            var seen = new HashSet<string>();
            foreach (var d in Q("SELECT Name, Manufacturer, PNPDeviceID, StatusInfo FROM Win32_SoundDevice"))
            {
                string pnp = S(d, "PNPDeviceID") ?? "";
                if (pnp.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) continue;
                string name = S(d, "Name");
                if (name == null || !seen.Add(pnp)) continue;
                string man = Real(S(d, "Manufacturer"));
                s.Add(man ?? "Устройство", name);
            }
            if (seen.Count == 0) s.Note("Устройства", "не найдены");
        }

        static List<ManagementBaseObject> Q(string wql, string scope = Cimv2)
        {
            using (var searcher = new ManagementObjectSearcher(scope, wql))
                return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }

        static ManagementBaseObject First(string wql, string scope = Cimv2) => Q(wql, scope).FirstOrDefault();

        static object P(ManagementBaseObject o, string name)
        {
            if (o == null) return null;
            try { return o[name]; } catch { return null; }
        }

        static string S(ManagementBaseObject o, string name)
        {
            var v = P(o, name);
            if (v == null) return null;
            string t = v.ToString().Trim();
            return t.Length == 0 ? null : t;
        }

        static ulong U(ManagementBaseObject o, string name)
        {
            var v = P(o, name);
            if (v == null) return 0;
            try { return Convert.ToUInt64(v); } catch { return 0; }
        }

        static RegistryKey Lm(string path)
        {
            try { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path); }
            catch { return null; }
        }

        static object RegO(RegistryKey k, string name)
        {
            try { return k?.GetValue(name); } catch { return null; }
        }

        static string RegS(RegistryKey k, string name)
        {
            string v = RegO(k, name)?.ToString().Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }

        static readonly string[] JunkParts =
        {
            "to be filled", "default string", "system product name", "system manufacturer", "system version",
            "not applicable", "not specified", "type1productconfigid", "type2 - board", "invalid", "chassis manufacture"
        };

        static readonly HashSet<string> JunkWhole = new HashSet<string>
        {
            "none", "unknown", "undefined", "n/a", "na", "x.x", "oem", "o.e.m.", "o.e.m", "0", "00000000",
            "123456789", "1234567890", "default", "-", ".", "not available"
        };

        public static string Real(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            string t = v.Trim();
            string l = t.ToLowerInvariant();
            if (JunkWhole.Contains(l)) return null;
            foreach (var j in JunkParts)
                if (l.IndexOf(j, StringComparison.Ordinal) >= 0) return null;
            return t;
        }

        static string StripMs(string v)
        {
            if (v == null) return null;
            foreach (var p in new[] { "Microsoft ", "Майкрософт " })
                if (v.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return v.Substring(p.Length).Trim();
            return v.Trim();
        }

        static string Squeeze(string v)
        {
            if (v == null) return null;
            v = string.Join(" ", v.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return v.Length == 0 ? null : v;
        }

        public static string Bin(ulong bytes)
        {
            double gb = bytes / 1073741824.0;
            if (gb >= 1024) return (gb / 1024).ToString("0.##", Ru) + " ТБ";
            if (gb >= 1) return gb.ToString("0.#", Ru) + " ГБ";
            double mb = bytes / 1048576.0;
            if (mb >= 1) return mb.ToString("0", Ru) + " МБ";
            return (bytes / 1024.0).ToString("0", Ru) + " КБ";
        }

        static string Dec(ulong bytes)
        {
            double gb = bytes / 1e9;
            if (gb >= 1000) return (gb / 1000).ToString("0.##", Ru) + " ТБ";
            return gb.ToString("0", Ru) + " ГБ";
        }

        static string KbMb(long bytes)
        {
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.##", Ru) + " МБ";
            return (bytes / 1024.0).ToString("0", Ru) + " КБ";
        }

        static string CimDate(string cim, bool withTime)
        {
            if (cim == null) return null;
            try { return ManagementDateTimeConverter.ToDateTime(cim).ToString(withTime ? "d MMMM yyyy, HH:mm" : "d MMMM yyyy", Ru); }
            catch { return DateOnly(cim); }
        }

        static string DateOnly(string cim)
        {
            if (cim == null || cim.Length < 8) return null;
            return DateTime.TryParseExact(cim.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("d MMMM yyyy", Ru)
                : null;
        }

        public static string Pl(long n, string one, string few, string many)
        {
            long a = Math.Abs(n) % 100, b = a % 10;
            string w = a > 10 && a < 20 ? many : b == 1 ? one : b >= 2 && b <= 4 ? few : many;
            return n + " " + w;
        }
    }
}
