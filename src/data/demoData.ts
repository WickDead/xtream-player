import type {
  AppBootstrap,
  ContinueWatchingItem,
  DownloadJob,
  FavoriteItem,
  LiveChannel,
  SeriesDetail,
  SeriesEpisode,
  SeriesItem,
  SeriesSeason,
  VodItem,
  XtreamProfile,
} from '../types/xtream'

export const demoProfiles: XtreamProfile[] = [
  {
    id: 'primary',
    name: 'Primary provider',
    serverUrl: 'https://iptv.example.com',
    username: 'profile-main',
    status: 'Connected',
    expiresAt: '2026-08-20',
    maxConnections: 4,
  },
  {
    id: 'backup',
    name: 'Travel backup',
    serverUrl: 'https://backup.example.net',
    username: 'profile-travel',
    status: 'Idle',
    expiresAt: '2026-09-12',
    maxConnections: 2,
  },
]

export const demoLiveChannels: LiveChannel[] = [
  {
    id: 'live-1',
    name: 'Open Sports 4K',
    category: 'Sports',
    currentProgram: 'West Coast Cup Final',
    nextProgram: 'Post-match analysis',
    bitrateLabel: '12.4 Mbps',
    streamUrl: 'https://iptv.example.com/live/open-sports.ts',
  },
  {
    id: 'live-2',
    name: 'Pacific Cinema HD',
    category: 'Movies',
    currentProgram: 'The Signal Bridge',
    nextProgram: 'Midnight Relay',
    bitrateLabel: '8.1 Mbps',
    streamUrl: 'https://iptv.example.com/live/pacific-cinema.m3u8',
  },
  {
    id: 'live-3',
    name: 'World News 24',
    category: 'News',
    currentProgram: 'Global Market Desk',
    nextProgram: 'Asia overnight wrap',
    bitrateLabel: '4.8 Mbps',
    streamUrl: 'https://iptv.example.com/live/world-news.ts',
  },
]

export const demoVodItems: VodItem[] = [
  {
    id: 'vod-1',
    title: 'Northern Passage',
    genre: 'Adventure',
    durationLabel: '1h 48m',
    quality: '4K HDR',
    description: 'A cold-water rescue mission turns into a long-haul survival run.',
    imageUrl: 'https://image.tmdb.org/t/p/w600_and_h900_bestv2/7BmQj8qE1FLuLTf7Xjf9sdIHzoa.jpg',
    streamUrl: 'https://iptv.example.com/movie/northern-passage.mp4',
  },
  {
    id: 'vod-2',
    title: 'The Signal Bridge',
    genre: 'Sci-Fi',
    durationLabel: '2h 07m',
    quality: '1080p',
    description: 'A relay station on the edge of the Pacific begins hearing tomorrow.',
    imageUrl: 'https://image.tmdb.org/t/p/w600_and_h900_bestv2/h1B7tW0t399VDjAcWJh8m87469b.jpg',
    streamUrl: 'https://iptv.example.com/movie/signal-bridge.mkv',
  },
]

export const demoSeriesItems: SeriesItem[] = [
  {
    id: 'series-1',
    title: 'Station 7',
    genre: 'Thriller',
    seasonCount: 3,
    episodeCount: 24,
    description: 'A harbor police unit uncovers a smuggling route hidden in ferry logs.',
    firstEpisodeTitle: 'S01E01 • Low Tide',
    imageUrl: 'https://image.tmdb.org/t/p/w600_and_h900_bestv2/q9HiiAqX3Rzv4Ztdeb998c8cETf.jpg',
    streamUrl: 'https://iptv.example.com/series/station-7/s01e01.mp4',
  },
  {
    id: 'series-2',
    title: 'Atlas Run',
    genre: 'Drama',
    seasonCount: 2,
    episodeCount: 16,
    description: 'A freight crew races to stabilize a collapsing orbital supply chain.',
    firstEpisodeTitle: 'S01E01 • First Burn',
    imageUrl: 'https://image.tmdb.org/t/p/w600_and_h900_bestv2/6CP91r8ooWKNFtwQes9VwmzuyL.jpg',
    streamUrl: 'https://iptv.example.com/series/atlas-run/s01e01.mp4',
  },
]

const demoSeriesSeasons: Record<string, SeriesSeason[]> = {
  'series-1': [
    {
      id: 'series-1-season-1',
      name: 'Season 1',
      seasonNumber: 1,
      episodeCount: 2,
      airDate: '2025-01-12',
      overview: 'The first harbor case pulls the team into a wider smuggling network.',
      cover: null,
    },
  ],
  'series-2': [
    {
      id: 'series-2-season-1',
      name: 'Season 1',
      seasonNumber: 1,
      episodeCount: 2,
      airDate: '2025-02-19',
      overview: 'A freight crew improvises around a destabilized orbital route.',
      cover: null,
    },
  ],
}

const demoSeriesEpisodes: Record<string, Record<string, SeriesEpisode[]>> = {
  'series-1': {
    '1': [
      {
        id: 'series-1-ep-1',
        seasonNumber: 1,
        episodeNumber: 1,
        title: 'S01E01 • Low Tide',
        plot: 'The unit finds a forged manifest tied to a missing trawler.',
        duration: '44m',
        releasedAt: '2025-01-12',
        containerExtension: 'mp4',
        image: null,
        streamUrl: 'https://iptv.example.com/series/station-7/s01e01.mp4',
      },
      {
        id: 'series-1-ep-2',
        seasonNumber: 1,
        episodeNumber: 2,
        title: 'S01E02 • Outer Marker',
        plot: 'A midnight boarding points back to a familiar shipping broker.',
        duration: '46m',
        releasedAt: '2025-01-19',
        containerExtension: 'mp4',
        image: null,
        streamUrl: 'https://iptv.example.com/series/station-7/s01e02.mp4',
      },
    ],
  },
  'series-2': {
    '1': [
      {
        id: 'series-2-ep-1',
        seasonNumber: 1,
        episodeNumber: 1,
        title: 'S01E01 • First Burn',
        plot: 'The crew diverts around a broken supply relay and loses contact with command.',
        duration: '52m',
        releasedAt: '2025-02-19',
        containerExtension: 'mp4',
        image: null,
        streamUrl: 'https://iptv.example.com/series/atlas-run/s01e01.mp4',
      },
      {
        id: 'series-2-ep-2',
        seasonNumber: 1,
        episodeNumber: 2,
        title: 'S01E02 • Blind Transfer',
        plot: 'A reroute through a military lane exposes the crew to inspection.',
        duration: '49m',
        releasedAt: '2025-02-26',
        containerExtension: 'mp4',
        image: null,
        streamUrl: 'https://iptv.example.com/series/atlas-run/s01e02.mp4',
      },
    ],
  },
}

export const demoContinueWatching: ContinueWatchingItem[] = [
  {
    id: 'cw-1',
    title: 'Northern Passage',
    kind: 'vod',
    detail: 'Resume at 01:14:22',
    progressPercent: 68,
  },
  {
    id: 'cw-2',
    title: 'Station 7',
    kind: 'series',
    detail: 'S02E05 • Resume at 00:19:08',
    progressPercent: 41,
  },
]

export const demoFavorites: FavoriteItem[] = [
  {
    id: 'fav-1',
    contentId: 'live-1',
    title: 'Open Sports 4K',
    kind: 'live',
  },
  {
    id: 'fav-2',
    contentId: 'vod-1',
    title: 'Northern Passage',
    kind: 'vod',
  },
]

export const demoDownloadJobs: DownloadJob[] = [
  {
    id: 'job-1',
    title: 'World Cup Qualifier recording',
    state: 'Queued',
    detail: 'Starts in 14 minutes',
  },
  {
    id: 'job-2',
    title: 'Northern Passage',
    state: 'Downloading',
    detail: '3.4 GB of 5.7 GB',
  },
]

export function createDemoBootstrap(): AppBootstrap {
  return {
    profiles: demoProfiles,
    favorites: demoFavorites,
    continueWatching: demoContinueWatching,
    diagnostics: [
      'Running in browser fallback mode with seeded demo data.',
      'Switch to the Tauri shell to use persisted profiles and Xtream API commands.',
      'Native playback target remains embedded mpv with external-player fallback.',
    ],
  }
}

export function getDemoSeriesDetail(seriesId: string): SeriesDetail | null {
  const series = demoSeriesItems.find((item) => item.id === seriesId)
  if (!series) {
    return null
  }

  return {
    seriesId: series.id,
    title: series.title,
    seasons: demoSeriesSeasons[series.id] ?? [],
    episodesBySeason: demoSeriesEpisodes[series.id] ?? {},
  }
}