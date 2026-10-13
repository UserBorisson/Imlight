-- Keep one native trainer and filter its already-loaded school rows locally.
-- Include only defines the entry point: Lua 5.1 cannot yield through Include's C frame.
function TowerRunTrainingTabs()
Include("API/Globals.lua");
Include("API/ControlClasses.lua");
Include("API/Debug.lua");
local school = TowerInitialSchool or "Fire";
local tabs = nil;
local trainer = nil;
local schoolOrder = {Fire=1,Ice=2,Storm=3,Myth=4,Life=5,Death=6,Balance=7,Astral=8};
local schools = {Fire=true,Ice=true,Storm=true,Myth=true,Life=true,Death=true,Balance=true,Astral=true};
local schoolBounds = {};
local maxRows = 9;
local maxPages = 32;
local function ReadProperty(control, property)
    if not control or not control.id then return nil; end
    local ok, value = pcall(function() return GetProperty(control.id, property); end);
    if ok then return value; end
    return nil;
end
local function PagePosition()
    if not trainer then return nil,nil; end
    local label = trainer.FindNamedWindow("PageNumber");
    local text = ReadProperty(label,"m_sText");
    if type(text) ~= "string" then return nil,nil; end
    local current,total = string.match(text,"(%d+)%s*/%s*(%d+)");
    return tonumber(current),tonumber(total);
end
local function SchoolFromTexture(texture)
    if type(texture) ~= "string" then return nil; end
    texture = string.lower(texture);
    for name,_ in pairs(schools) do
        if name == "Astral" then
            if string.find(texture,"sun",1,true) or string.find(texture,"star",1,true)
                or string.find(texture,"astral",1,true) then return name; end
        elseif string.find(texture,string.lower(name),1,true) then
            return name;
        end
    end
    return nil;
end
local function OptionSchool(index)
    if not trainer then return nil; end
    local option = trainer.FindNamedWindow("Option_"..index);
    if not option then return nil; end
    local icon = option.FindNamedWindow("SchoolIcon");
    local texture = ReadProperty(icon,"m_pMaterial.m_pDiffuseMap.m_sFilename");
    return SchoolFromTexture(texture);
end
local function PageHasSchool(name)
    for index=1,maxRows do
        if OptionSchool(index) == name then return true; end
    end
    return false;
end
local function FilterRows(name)
    for index=1,maxRows do
        local option = trainer and trainer.FindNamedWindow("Option_"..index) or nil;
        if option then option.ShowWindow(OptionSchool(index) == name); end
    end
end
local function ClickPage(direction)
    local button = trainer and trainer.FindNamedWindow(direction) or nil;
    if not button then return false; end
    local before = PagePosition();
    button.SetButtonDown(true);
    button.SetButtonDown(false);
    Sleep(0.02);
    local after = PagePosition();
    return before and after and before ~= after;
end
local function SetPageControls(name, firstPage, lastPage)
    local current = PagePosition();
    local pageUp = trainer.FindNamedWindow("PageUp");
    local pageDown = trainer.FindNamedWindow("PageDown");
    local pageNumber = trainer.FindNamedWindow("PageNumber");
    local hasRows = firstPage and lastPage and current and current >= firstPage and current <= lastPage;
    if pageUp then pageUp.SetEnable(hasRows and current > firstPage or false); end
    if pageDown then pageDown.SetEnable(hasRows and current < lastPage or false); end
    if pageNumber and hasRows then
        local relative = current-firstPage+1;
        SetProperty(pageNumber.id,"m_sText",relative.."/"..(lastPage-firstPage+1));
    end
end
local function SetTabVisuals(name)
    if not tabs then return; end
    for tab,_ in pairs(schools) do
        local button = tabs.FindNamedWindow("TowerSchool_"..tab);
        if button then
            -- Grey the selected school with its native desaturated material state,
            -- while keeping every tab enabled so clicks never fade the whole icon.
            button.SetEnable(true);
            button.SetGreyed(tab == name);
        end
    end
end
local function NavigateToSchool(name, fromSchool)
    if not trainer then return; end
    local current = PagePosition();
    if not current then
        Log("TowerTraining: native page number is unavailable; filtering current page only");
        FilterRows(name);
        SetTabVisuals(name);
        return;
    end
    local cached = schoolBounds[name];
    if cached then
        while current > cached[1] do
            if not ClickPage("PageUp") then break; end
            current = PagePosition();
        end
        while current < cached[1] do
            if not ClickPage("PageDown") then break; end
            current = PagePosition();
        end
        school = name;
        FilterRows(name);
        SetPageControls(name,cached[1],cached[2]);
        SetTabVisuals(name);
        return;
    end
    local direction = (schoolOrder[name] or 1) >= (schoolOrder[fromSchool] or 1) and "PageDown" or "PageUp";
    local found = PageHasSchool(name);
    for _=1,maxPages do
        if found then break; end
        if not ClickPage(direction) then break; end
        found = PageHasSchool(name);
    end
    if not found then
        Log("TowerTraining: no eligible rows for "..name);
        FilterRows(name);
        SetTabVisuals(name);
        SetPageControls(name,nil,nil);
        return;
    end

    local firstPage = PagePosition();
    local lastPage = firstPage;
    if direction == "PageDown" then
        for _=1,maxPages do
            if not ClickPage("PageDown") then break; end
            if PageHasSchool(name) then
                lastPage = PagePosition();
            else
                ClickPage("PageUp");
                break;
            end
        end
        while PagePosition() > firstPage do
            if not ClickPage("PageUp") then break; end
        end
    else
        for _=1,maxPages do
            if not ClickPage("PageUp") then break; end
            if PageHasSchool(name) then
                firstPage = PagePosition();
            else
                ClickPage("PageDown");
                break;
            end
        end
    end
    schoolBounds[name] = {firstPage,lastPage};
    school = name;
    FilterRows(name);
    SetPageControls(name,firstPage,lastPage);
    SetTabVisuals(name);
    Log("TowerTraining: locally filtered "..name.." page "..tostring(PagePosition()));
end
local function AttachTowerTrainingTabs()
    local id = FindNamedWindow("NPCTrainingGUI");
    if not id or id == 0 then return; end
    trainer = OpenClass(id);
    if not trainer then return; end
    local old = trainer.FindNamedWindow("TowerTrainingSchoolTabs");
    if old then old.DetachSelf(); end
    tabs = CreateGUIClass("TowerTrainingSchoolTabs.gui",true);
    tabs.DetachSelf();
    trainer.AttachWindow(tabs);
    Log("TowerTraining: school tabs attached to the native trainer");
    NavigateToSchool(school,school);
    Server("towerui opened");
end
RegisterEvent("OnTrainingOpened");
RegisterEvent("WB_BUTTONUP");
AttachTowerTrainingTabs();
while true do
    local event = GetEvent();
    if event.EventName == "OnTrainingOpened" then
        schoolBounds = {};
        AttachTowerTrainingTabs();
    elseif event.EventName == "WB_BUTTONUP" then
        local name = event.Name or "";
        local selected = string.match(name,"^TowerSchool_(%a+)$");
        if selected and schools[selected] and selected ~= school then
            NavigateToSchool(selected,school);
        elseif name == "PageUp" or name == "PageDown" then
            if not PageHasSchool(school) then
                ClickPage(name == "PageDown" and "PageUp" or "PageDown");
            end
            local bounds = schoolBounds[school];
            FilterRows(school);
            SetPageControls(school,bounds and bounds[1] or nil,bounds and bounds[2] or nil);
        elseif name == "ExitButton" or name == "Exit" then
            Server("towerui close");
            break;
        end
    end
end
if tabs then tabs.DetachSelf(); end
UnregisterEvent("OnTrainingOpened");
UnregisterEvent("WB_BUTTONUP");
end
