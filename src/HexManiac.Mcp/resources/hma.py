
# these fuctions are loaded automatically and are available
# from the python automation tool or python table buttons

# if running from a table button, the argument will be the
# current element in the current table as a ModelArrayElement

# note: this runs on pythonnet (real CPython), not IronPython. C# extension
# methods can no longer be called as instance methods (no clr.ImportExtensions) -
# call them as static methods instead, e.g. SomeExtensionClass.Method(obj, args).
