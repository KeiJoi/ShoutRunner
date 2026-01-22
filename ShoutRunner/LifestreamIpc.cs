using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace ShoutRunner;

public sealed class LifestreamIpc
{
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<bool> isBusy;

    public LifestreamIpc(IDalamudPluginInterface pluginInterface)
    {
        changeWorld = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
    }

    public bool TryChangeWorld(string world)
    {
        try
        {
            return changeWorld.InvokeFunc(world);
        }
        catch (IpcNotReadyError)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryIsBusy(out bool busy)
    {
        try
        {
            busy = isBusy.InvokeFunc();
            return true;
        }
        catch (IpcNotReadyError)
        {
            busy = false;
            return false;
        }
        catch (Exception)
        {
            busy = false;
            return false;
        }
    }
}
