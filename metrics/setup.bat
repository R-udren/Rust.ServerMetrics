@echo off
python -B "%~dp0setup.py" %*
exit /b %errorlevel%
