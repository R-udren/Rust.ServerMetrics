@echo off
python -B "%~dp0check.py" %*
exit /b %errorlevel%
