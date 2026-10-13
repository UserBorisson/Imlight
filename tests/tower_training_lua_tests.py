"""Exercise the client process with Lua 5.1's native non-yieldable Include boundary.

Run with lupa.lua51 available on PYTHONPATH. No game client or database is used.
"""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

from lupa.lua51 import LuaRuntime


SOURCE = Path(__file__).resolve().parents[1] / "content/tower-native-ui/Scripts"


class TrainingProcessTests(unittest.TestCase):
    def test_school_controls_use_native_repeated_child_entries(self):
        root = ET.parse(SOURCE.parent / "GUI/TowerTrainingSchoolTabs.gui").getroot().find("Class")
        entries = root.findall("m_Children")
        self.assertEqual(8, len(entries), "native loader reads one control per m_Children entry")
        self.assertTrue(all(len(entry.findall("Class")) == 1 for entry in entries))
        names = [entry.findtext("Class/m_sName") for entry in entries]
        self.assertEqual(8, len(set(names)))

    def test_every_school_process_waits_then_handles_open_switch_and_close(self):
        for school in ("Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Astral"):
            with self.subTest(school=school):
                lua = LuaRuntime(unpack_returned_tuples=True)

                def include(name):
                    if name.startswith("Tower/"):
                        lua.execute((SOURCE / name).read_text())

                lua.globals().Include = include
                lua.execute('''
                    commands = {}; attached = 0; visible = false;
                    function RegisterEvent(name) end
                    function UnregisterEvent(name) end
                    function Log(message) end
                    function GetEvent() return coroutine.yield("waiting") end
                    function FindNamedWindow(name) return visible and 1 or 0 end
                    parent = {
                        FindNamedWindow=function(name) return nil end,
                        AttachWindow=function(window)
                            assert(window.detached, "detach the initially loaded desktop window before reparenting");
                            attached=attached+1
                        end
                    };
                    function OpenClass(id) return parent end
                    function CreateGUIClass(name, visible)
                        local window={detached=false};
                        window.FindNamedWindow=function(name)
                                    return setmetatable({SetEnable=function(enabled) end},
                                        {__newindex=function() error("PF_NOSCRIPT") end});
                                end;
                        window.DetachSelf=function() window.detached=true end;
                        return window;
                    end
                    function Server(command) table.insert(commands, command) end
                ''')
                script = (SOURCE / f"Tower/TrainingTabs_{school}.lua").read_text()
                # The client loads scripts synchronously, then starts main as a coroutine.
                # Yielding during load fails even when Include itself has already returned.
                lua.execute(script)
                self.assertIsNotNone(lua.globals().main, "native client process requires main()")
                ok, result = lua.execute("process=coroutine.create(main); return coroutine.resume(process)")
                self.assertTrue(ok, result)
                self.assertEqual("waiting", result)
                lua.execute('visible=true; assert(coroutine.resume(process, {EventName="OnTrainingOpened"}))')
                self.assertEqual(1, lua.globals().attached)
                self.assertEqual("towerui opened", lua.globals().commands[1])
                lua.execute('assert(coroutine.resume(process, {EventName="WB_BUTTONUP",Name="TowerSchool_Ice"}))')
                self.assertEqual("towerui school Ice", lua.globals().commands[2])
                lua.execute('assert(coroutine.resume(process, {EventName="WB_BUTTONUP",Name="ExitButton"}))')
                self.assertEqual("towerui close", lua.globals().commands[3])
                self.assertEqual("dead", lua.eval("coroutine.status(process)"))


if __name__ == "__main__":
    unittest.main()
