@echo off
python -B "%~dp0verify.py" %*
exit /b %errorlevel%
