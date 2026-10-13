"""Exercise the client process with Lua 5.1's native non-yieldable Include boundary.

Run with lupa.lua51 available on PYTHONPATH. No game client or database is used.
"""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

from lupa.lua51 import LuaRuntime


SOURCE = Path(__file__).resolve().parents[1] / "content/tower-native-ui/Scripts"


class TrainingProcessTests(unittest.TestCase):
    def test_tabs_do_not_synthesize_a_general_npc_interaction(self):
        for script in (SOURCE / "Tower").glob("*.lua"):
            self.assertNotIn('SendEvent("NPCInteract")',script.read_text())

    def test_school_controls_use_native_repeated_child_entries(self):
        root = ET.parse(SOURCE.parent / "GUI/TowerTrainingSchoolTabs.gui").getroot().find("Class")
        entries = root.findall("m_Children")
        self.assertEqual(8, len(entries), "native loader reads one control per m_Children entry")
        self.assertTrue(all(len(entry.findall("Class")) == 1 for entry in entries))
        names = [entry.findtext("Class/m_sName") for entry in entries]
        self.assertEqual(8, len(set(names)))

    def test_school_tabs_filter_the_single_native_list_locally(self):
        for school in ("Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Astral"):
            with self.subTest(school=school):
                lua = LuaRuntime(unpack_returned_tuples=True)

                def include(name):
                    if name.startswith("Tower/"):
                        lua.execute((SOURCE / name).read_text())

                lua.globals().Include = include
                lua.execute('''
                    commands = {}; attached = 0; visible = false; currentPage = 1;
                    visibleRows = {}; rowSchools = {};
                    pages = {
                        [1] = {"Fire","Fire","Ice","Ice","Storm","Myth","Life","Life","Life"},
                        [2] = {"Death","Death","Balance","Balance","Astral","Astral","Astral","Astral"}
                    };
                    function RegisterEvent(name) end
                    function UnregisterEvent(name) end
                    function Log(message) table.insert(logs,message) end
                    logs = {};
                    function GetEvent() return coroutine.yield("waiting") end
                    function FindNamedWindow(name) return visible and 1 or 0 end
                    function SetProperty(id, property, value)
                        assert(id == 50 and property == "m_sText"); pageLabel = value
                    end
                    function GetProperty(id, property)
                        if id == 50 and property == "m_sText" then return tostring(currentPage).."/2" end
                        if rowSchools[id] and property == "m_pMaterial.m_pDiffuseMap.m_sFilename" then
                            return "GUI/Schools/Button_"..rowSchools[id]..".dds"
                        end
                        return nil
                    end
                    function makeButton()
                        return {SetEnable=function(enabled) end, SetGreyed=function(greyed) end}
                    end
                    local trainer = {
                        FindNamedWindow=function(name)
                            local i = tonumber(string.match(name,"^Option_(%d+)$"));
                            if i then
                                local school = pages[currentPage][i];
                                local row = {id=100+i, FindNamedWindow=function(child)
                                    if child ~= "SchoolIcon" then return nil end
                                    rowSchools[100+i] = school;
                                    return {id=100+i};
                                end, ShowWindow=function(show) visibleRows[i]=show end};
                                return row;
                            end
                            if name == "PageNumber" then return {id=50} end
                            if name == "PageUp" or name == "PageDown" then
                                local delta = name == "PageUp" and -1 or 1;
                                local down = false;
                                return {SetEnable=function(enabled) end, SetButtonDown=function(value)
                                    if value then down=true elseif down then
                                        currentPage=math.max(1,math.min(2,currentPage+delta)); down=false;
                                    end
                                end};
                            end
                            return nil;
                        end,
                        AttachWindow=function(window)
                            assert(window.detached, "detach the initially loaded desktop window before reparenting");
                            attached=attached+1
                        end
                    };
                    function OpenClass(id) return trainer end
                    function CreateGUIClass(name, visible)
                        local window={detached=false};
                        window.FindNamedWindow=function(name)
                                    local key=string.match(name,"^TowerSchool_(%a+)$");
                                    if key then
                                        return {SetEnable=function(enabled) assert(enabled) end,
                                            SetGreyed=function(greyed) tabGreyed[key]=greyed end};
                                    end
                                    return nil;
                                end;
                        window.DetachSelf=function() window.detached=true end;
                        return window;
                    end
                    function Server(command) table.insert(commands, command) end
                    function Sleep(seconds) end
                    tabGreyed = {};
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
                self.assertTrue(lua.globals().tabGreyed[school], "selected school uses its native greyed state")
                for i in range(1,10):
                    row_school = lua.globals().rowSchools[100+i]
                    self.assertEqual(bool(row_school == school), bool(lua.globals().visibleRows[i]),
                                     "the first view contains only the initial school")
                target = "Ice" if school == "Death" else "Death"
                lua.execute(f'assert(coroutine.resume(process, {{EventName="WB_BUTTONUP",Name="TowerSchool_{target}"}}))')
                self.assertEqual(1, len(lua.globals().commands), "school changes do not request a server window refresh")
                self.assertEqual(2 if target in ("Death","Balance","Astral") else 1, lua.globals().currentPage)
                self.assertTrue(lua.globals().tabGreyed[target], "new school uses native greyed state")
                for i in range(1,10):
                    visible_row = lua.globals().visibleRows[i]
                    row_school = lua.globals().rowSchools[100+i]
                    self.assertEqual(bool(row_school == target), bool(visible_row),
                                     "the visible native rows contain only the selected school")
                lua.execute('assert(coroutine.resume(process, {EventName="WB_BUTTONUP",Name="ExitButton"}))')
                self.assertEqual("towerui close", lua.globals().commands[2])
                self.assertEqual("dead", lua.eval("coroutine.status(process)"))


if __name__ == "__main__":
    unittest.main()
