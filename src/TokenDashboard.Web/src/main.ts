import { createApp } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import './styles.css'

const routeShell = { render: () => null }
const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/dashboard' },
    { path: '/dashboard', component: routeShell },
    { path: '/pricing', component: routeShell },
    { path: '/:pathMatch(.*)*', redirect: '/dashboard' }
  ]
})

createApp(App).use(router).mount('#app')
