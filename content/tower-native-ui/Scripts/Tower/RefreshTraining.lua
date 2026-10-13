-- Run after the old trainer closes and the replacement NPC options are installed.
-- NPCInteract is the native void event; SendEvent queues it on the client event loop.
function main()
    local oldWindow=FindNamedWindow("NPCTrainingGUI");
    Log("TowerTraining: waiting for old trainer window "..tostring(oldWindow));
    -- Native close is asynchronous. Interacting while its controller still exists
    -- toggles that old interaction instead of opening the replacement inventory.
    for attempt=1,100 do
        local current=FindNamedWindow("NPCTrainingGUI");
        if not current or current==0 then
            Log("TowerTraining: old trainer gone; reopening native trainer");
            SendEvent("NPCInteract");
            return;
        end
        if oldWindow and oldWindow~=0 and current~=oldWindow then
            Log("TowerTraining: refresh canceled; another trainer window was opened");
            return;
        end
        Sleep(0.1);
    end
    Log("TowerTraining: refresh canceled; old trainer did not close");
end
