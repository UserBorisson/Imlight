-- Tower-only tab bar attached to the existing native trainer window.
-- Include only defines the entry point: Lua 5.1 cannot yield through Include's C frame.
function TowerRunTrainingTabs()
Include("API/Globals.lua");
Include("API/ControlClasses.lua");
Include("API/Debug.lua");
local school = TowerInitialSchool or "Fire";
local tabs = nil;
local schools = {Fire=true,Ice=true,Storm=true,Myth=true,Life=true,Death=true,Balance=true,Astral=true};
Log("TowerTraining: process started for "..school);
function AttachTowerTrainingTabs()
    local id=FindNamedWindow("NPCTrainingGUI");
    if not id or id==0 then return; end
    local parent=OpenClass(id);
    if not parent then return; end
    local old=parent.FindNamedWindow("TowerTrainingSchoolTabs");
    if old then old.DetachSelf(); end
    tabs=CreateGUIClass("TowerTrainingSchoolTabs.gui",true);
    tabs.DetachSelf();
    parent.AttachWindow(tabs);
    Log("TowerTraining: school tabs attached");
    Server("towerui opened");
    for name,_ in pairs(schools) do
        local button=tabs.FindNamedWindow("TowerSchool_"..name);
        if button then button.SetEnable(name~=school); end
    end
end
RegisterEvent("OnTrainingOpened");
RegisterEvent("WB_BUTTONUP");
AttachTowerTrainingTabs();
while true do
    local event=GetEvent();
    if event.EventName=="OnTrainingOpened" then
        AttachTowerTrainingTabs();
    elseif event.EventName=="WB_BUTTONUP" then
        local name=event.Name or "";
        local selected=string.match(name,"^TowerSchool_(%a+)$");
        if selected and schools[selected] then
            school=selected;
            Log("TowerTraining: selected school "..selected);
            Server("towerui school "..selected);
        elseif name=="ExitButton" or name=="Exit" then
            Server("towerui close");
            break;
        end
    end
end
if tabs then tabs.DetachSelf(); end
UnregisterEvent("OnTrainingOpened");
UnregisterEvent("WB_BUTTONUP");
end
