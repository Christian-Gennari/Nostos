module.exports = {
  apps: [{
    name: 'nostos',
    script: 'npm',
    args: 'run prod',
    cwd: '/home/dev/coding/projects/Nostos',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: 'http://0.0.0.0:5214',
      version: ''
    }
  }]
}