<script setup lang="ts">
import { Card } from '@/components/ui/card'
</script>

<template>
  <Card class="mb-6 min-w-0 gap-3 border-border/60 bg-card/80 p-5 backdrop-blur-sm">
    <slot />
  </Card>
</template>
