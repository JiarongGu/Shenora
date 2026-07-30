using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class OperationExceptionTests
{
    [Fact]
    public void Message_falls_back_to_code()
    {
        var ex = new OperationException("IMPORT_FAILED");

        Assert.Equal("IMPORT_FAILED", ex.Code);
        Assert.Equal("IMPORT_FAILED", ex.Message);
        Assert.Null(ex.Parameters);
    }

    [Fact]
    public void Single_parameter_convenience_ctor()
    {
        var ex = new OperationException("IMPORT_FAILED", "name", "MyThing");

        Assert.Equal("MyThing", ex.Parameters!["name"]);
    }

    [Fact]
    public void Inner_exception_is_preserved()
    {
        var inner = new IOException("disk gone");
        var ex = new OperationException("IMPORT_FAILED", null, "import blew up", inner);

        Assert.Same(inner, ex.InnerException);
        Assert.Equal("import blew up", ex.Message);
    }

    [Fact]
    public void ToError_maps_code_and_parameters()
    {
        var ex = new OperationException("IMPORT_FAILED", "name", "MyThing", "import blew up");
        var error = ex.ToError();

        Assert.Equal("IMPORT_FAILED", error.Code);
        Assert.Equal("import blew up", error.Message);
        Assert.Equal("MyThing", error.Parameters!["name"]);
    }

    [Fact]
    public void ToError_omits_message_when_it_is_just_the_code()
    {
        // No explicit message → Exception.Message echoes the code; the wire form drops the echo.
        var error = new OperationException("IMPORT_FAILED").ToError();

        Assert.Equal("IMPORT_FAILED", error.Code);
        Assert.Null(error.Message);
    }

    [Fact]
    public void Derived_exceptions_are_still_operation_exceptions()
    {
        // Unsealed on purpose: apps derive domain error types, the dispatch boundary catches the base.
        OperationException ex = new AppSpecificException();
        Assert.Equal("APP_SPECIFIC", ex.ToError().Code);
    }

    private sealed class AppSpecificException() : OperationException("APP_SPECIFIC");
}
