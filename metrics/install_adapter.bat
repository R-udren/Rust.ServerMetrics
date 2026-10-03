@echo off
python -B "%~dp0install_adapter.py" %*
exit /b %errorlevel%
