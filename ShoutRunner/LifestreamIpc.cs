using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace ShoutRunner;

public sealed class LifestreamIpc
{
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, bool> canVisitCrossDC;
    private readonly ICallGateSubscriber<string, bool> canVisitSameDC;

    public LifestreamIpc(IDalamudPluginInterface pluginInterface)
    {
        changeWorld = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        canVisitCrossDC = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");
        canVisitSameDC = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
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

    public bool TryCanVisitCrossDC(string world, out bool isCrossDC)
    {
        try
        {
            isCrossDC = canVisitCrossDC.InvokeFunc(world);
            return true;
        }
        catch (IpcNotReadyError)
        {
            isCrossDC = false;
            return false;
        }
        catch (Exception)
        {
            isCrossDC = false;
            return false;
        }
    }

    public bool TryCanVisitSameDC(string world, out bool isSameDC)
    {
        try
        {
            isSameDC = canVisitSameDC.InvokeFunc(world);
            return true;
        }
        catch (IpcNotReadyError)
        {
            isSameDC = false;
            return false;
        }
        catch (Exception)
        {
            isSameDC = false;
            return false;
        }
    }
}
