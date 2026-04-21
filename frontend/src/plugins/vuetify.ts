import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'
import { createVuetify } from 'vuetify'

export const vuetify = createVuetify({
  theme: {
    defaultTheme: 'aibuilderDark',
    themes: {
      aibuilderDark: {
        dark: true,
        colors: {
          background: '#0b0d10',
          surface: '#14171c',
          primary: '#6aa8ff',
          secondary: '#c4b5fd',
          success: '#5bd399',
          warning: '#f1b24b',
          error: '#ef6a6a',
        },
      },
    },
  },
  defaults: {
    VBtn: { variant: 'flat', rounded: 'md' },
    VCard: { rounded: 'lg', border: 'thin' },
    VTextField: { variant: 'outlined', density: 'comfortable', hideDetails: 'auto' },
    VTextarea: { variant: 'outlined', density: 'comfortable', hideDetails: 'auto' },
  },
})
