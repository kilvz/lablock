const Router = {
  routes: {},

  register(name, handler) {
    this.routes[name] = handler;
  },

  navigate(hash) {
    const route = hash.replace('#', '') || 'dashboard';
    const container = document.getElementById('content');

    const navItems = document.querySelectorAll('.nav-item');
    navItems.forEach(item => {
      item.classList.toggle('active', item.dataset.route === route);
    });

    if (this.routes[route]) {
      container.innerHTML = '<div class="loading">Loading...</div>';
      this.routes[route](container);
    } else {
      container.innerHTML = '<div class="loading">Page not found</div>';
    }
  },

  init() {
    window.addEventListener('hashchange', () => this.navigate(window.location.hash));
    this.navigate(window.location.hash);
  }
};
