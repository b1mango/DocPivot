using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace DocPivot.OfficeWorker;

internal static class ComObject
{
    public static void SetProperty(object value, string propertyName, object propertyValue)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        try
        {
            _ = value.GetType().InvokeMember(
                propertyName,
                BindingFlags.SetProperty,
                binder: null,
                target: value,
                args: [propertyValue],
                culture: CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is COMException)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new OfficeWorkerException(
                "OFFICE_COM_BINDING_FAILURE",
                $"Office property '{propertyName}' could not be set ({exception.InnerException.GetType().Name}).",
                true);
        }
        catch (COMException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new OfficeWorkerException(
                "OFFICE_COM_BINDING_FAILURE",
                $"Office property '{propertyName}' could not be set ({exception.GetType().Name}).",
                true);
        }
    }

    public static void FinalRelease(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            if (!Marshal.IsComObject(value))
            {
                return;
            }

            _ = Marshal.FinalReleaseComObject(value);
        }
        catch (InvalidComObjectException)
        {
            // A previous cleanup path already released this RCW.
        }
    }
}
